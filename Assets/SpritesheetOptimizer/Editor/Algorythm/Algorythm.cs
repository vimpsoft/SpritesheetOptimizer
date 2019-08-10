﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class Algorythm
{
    public readonly ProgressReport OverallProgressReport;
    public readonly ProgressReport OperationProgressReport;

    public int UnprocessedPixels { get; private set; }

    /// <summary>
    /// Сколько раз можно воспользоваться списком областей перед их устареванием
    /// </summary>
    private readonly int _areasFreshmentSpan;

    /// <summary>
    /// Сколько областей могут быть предположительно затронуты удалением одной области с картинки и должны быть обновлены перед переупорядочиванием
    /// </summary>
    private readonly int _areasVolatilityRange;

    private readonly MyColor[][][] _sprites;
    private readonly IList<ISizingsConfigurator> _sizingsConfigurators;
    private readonly IList<IScoreCounter> _scoreCounters;
    private readonly Type _areaEnumeratorType;

    private ConcurrentDictionary<int, MyArea> _allAreas;
    //private IOrderedEnumerable<KeyValuePair<int, long>> _allScores;
    private IEnumerable<MyVector2> _areaSizings;
    private IAreaEnumerator _areaEnumerator;

    private CancellationToken _ct;

    public Algorythm(MyColor[][][] sprites, Type areaEnumeratorType, IList<ISizingsConfigurator> sizingConfigurators, IList<IScoreCounter> scoreCounters, int areasFreshmentSpan, int areasVolatilityRange)
    {
        OperationProgressReport = new ProgressReport();
        OverallProgressReport = new ProgressReport();

        _areasFreshmentSpan = areasFreshmentSpan;
        _areasVolatilityRange = areasVolatilityRange;

        _sprites = sprites;
        _areaEnumeratorType = areaEnumeratorType;
        _sizingsConfigurators = sizingConfigurators;
        _scoreCounters = scoreCounters;
    }

    public async Task Initialize(Vector2Int maxAreaSize, CancellationToken ct)
    {
        _ct = ct;
        _areaSizings = getAreaSizings(_sprites, maxAreaSize, ct);

        var areaEnumeratorCtor = _areaEnumeratorType.GetConstructor(new Type[] { typeof(MyColor[][][]) });
        foreach (var param in areaEnumeratorCtor.GetParameters())
        {
            Debug.Log($"ParameterType = {param.ParameterType}");
        }
        Debug.Log($"_sprites = {_sprites}. Type = {_sprites.GetType()}");
        if (areaEnumeratorCtor == null)
            areaEnumeratorCtor = _areaEnumeratorType.GetConstructor(new Type[] { typeof(MyColor[][][]), typeof(IEnumerable<MyVector2>) });
        else
            _areaEnumerator = (IAreaEnumerator)areaEnumeratorCtor.Invoke(new object[] { _sprites });
        //_areaEnumerator = (IAreaEnumerator)Activator.CreateInstance(_areaEnumeratorType, new object[] { _sprites });
        if (_areaEnumerator == null && areaEnumeratorCtor == null)
            throw new ArgumentException($"Got AreaEnumerator with unknown set of ctor parameters.");
        else if (_areaEnumerator == null)
            _areaEnumerator = (IAreaEnumerator)areaEnumeratorCtor.Invoke(new object[] { _sprites, _areaSizings });
        //_areaEnumerator = (IAreaEnumerator)Activator.CreateInstance(_areaEnumeratorType, _sprites, _areaSizings);
        UnprocessedPixels = countUprocessedPixels(MyVector2.One, _areaEnumerator);
    }

    #region Initializing

    private IEnumerable<MyVector2> getAreaSizings(MyColor[][][] sprites, Vector2Int maxAreaSize, CancellationToken ct)
    {
        var result = default(IEnumerable<MyVector2>);
        for (int i = 0; i < _sizingsConfigurators.Count && !ct.IsCancellationRequested; i++)
            result = _sizingsConfigurators[i]?.ConfigureSizings(result, sprites.Length, maxAreaSize.x, maxAreaSize.y, ct);
        return result;
    }

    private int countUprocessedPixels(MyVector2 areaSizing, IAreaEnumerator areaEnumerator)
    {
        var result = 0;
        areaEnumerator.Enumerate(areaSizing, (sprite, index, x, y) =>
        {
            if (sprite[x][y].A > 0)
                result++;
        });
        return result;
    }

    #endregion Initializing

    public async Task<Dictionary<MyArea, List<MyAreaCoordinates>>> Run()
    {
        OverallProgressReport.OperationDescription = "Removing areas from picture";
        OverallProgressReport.OperationsCount = UnprocessedPixels;
        var map = new Dictionary<MyArea, List<MyAreaCoordinates>>();

        //var areasOrderedByScores = _allAreas.OrderByDescending(kvp => kvp.Value.Correlations.Count * kvp.Value.Score).ToList();

        var currentAreaIndex = 0;

        List<KeyValuePair<int, MyArea>> orderedAreas = null;
        while (UnprocessedPixels > 0)
        {
            if (_ct.IsCancellationRequested)
                break;
            if (currentAreaIndex % _areasFreshmentSpan == 0)
            {
                Debug.Log($"Areas and scores recounting...");
                await setAreasAndScores();
            }

            //После удаления некоторых пикселей рейтинги областей могут меняться - поэтому надо обновлять и переупорядочивать каждый раз.
            if (orderedAreas != null)
            {
                OperationProgressReport.OperationDescription = "Updating volatile scores";
                OperationProgressReport.OperationsCount = _areasVolatilityRange;
                OperationProgressReport.OperationsDone = 0;
                Parallel.For(0, _areasVolatilityRange, (i, loopState) =>
                {
                    if (_ct.IsCancellationRequested)
                        loopState.Break();

                    var invalidAreas = new List<int>();
                    var area = orderedAreas[i].Value;
                    foreach (var kvp in area.Correlations)
                    {
                        var correlation = kvp.Value;
                        var sprite = _sprites[correlation.SpriteIndex];
                        var correlatedArea = MyArea.CreateFromSprite(sprite, correlation.X, correlation.Y, correlation.Dimensions);
                        if (correlatedArea.GetHashCode() != area.GetHashCode())
                            invalidAreas.Add(kvp.Key);
                    }
                    for (int j = 0; j < invalidAreas.Count; j++)
                    {
                        MyAreaCoordinates val;
                        area.Correlations.TryRemove(invalidAreas[j], out val);
                    }
                    invalidAreas.Clear();
                    OperationProgressReport.OperationsDone++;
                });
            }
            orderedAreas?.Clear();
            orderedAreas = _allAreas.OrderByDescending(kvp => kvp.Value.Correlations.Count * kvp.Value.Score).ToList();
            var currentArea = orderedAreas[0];

            Debug.Log($"Working with score #{currentAreaIndex}: id {currentArea.Key}, score {currentArea.Value.Correlations.Count * currentArea.Value.Score}");
            var areasRemoved = await applyBestArea(_sprites, currentArea.Key);
            var pixelsRemoved = currentArea.Value.OpaquePixelsCount * areasRemoved.Count;
            map.Add(currentArea.Value, areasRemoved);
            OverallProgressReport.OperationsDone += pixelsRemoved;
            UnprocessedPixels -= pixelsRemoved;
            currentAreaIndex++;
        }
        Debug.Log($"Done!");

        return map;
    }

    private async Task setAreasAndScores()
    {
        _allAreas = await Task.Run(() => getAllAreas(_sprites, _areaSizings, _areaEnumerator, OperationProgressReport));
    }

    private ConcurrentDictionary<int, MyArea> getAllAreas(MyColor[][][] sprites, IEnumerable<MyVector2> areaSizings, IAreaEnumerator areaEnumerator, ProgressReport progressReport)
    {
        var areas = new ConcurrentDictionary<int, MyArea>();

        var overallOpsCount = areaSizings.Count() * sprites.Length;

        progressReport.OperationDescription = "Fetching possible areas";
        progressReport.OperationsCount = overallOpsCount;
        progressReport.OperationsDone = 0;

        var sizingsList = areaSizings.ToList();

        var allAreas = 0;
        var uniqueAreas = 0;
        try
        {
            Parallel.For(0, overallOpsCount, (int index, ParallelLoopState state) =>
            {
                if (state.IsExceptional)
                    Debug.Log("Exception!");
                if (_ct.IsCancellationRequested)
                    state.Break();
                var sizingIndex = Mathf.FloorToInt(index / sprites.Length);
                var spriteIndex = index - sizingIndex * sprites.Length;
                if (sizingIndex > sizingsList.Count - 1)
                    Debug.LogError($"sizingsList[sizingIndex] is out of range! sizingIndex = {sizingIndex}, sizingsList.Count = {sizingsList.Count}");
                if (sizingIndex < 0)
                    Debug.LogError($"sizingIndex < 0! ({sizingIndex})");
                var targetSizing = sizingsList[sizingIndex];
                var spritesAreas = getUniqueAreas(targetSizing, spriteIndex, areas, areaEnumerator, progressReport);
                allAreas += spritesAreas.total;
                uniqueAreas += spritesAreas.unique;
            });
        }
        catch (AggregateException ae)
        {
            Debug.Log("catch");
            ae.Handle((inner) =>
            {
                Debug.LogError($"{inner.Message}\r\n\r\n{inner.StackTrace}");
                return true;
            });
        }
        return areas;
    }

    /// <returns>(Overall areas, Unique areas)</returns>
    private (int total, int unique) getUniqueAreas(MyVector2 areaSizing, int spriteIndex, ConcurrentDictionary<int, MyArea> areas, IAreaEnumerator areaEnumerator, ProgressReport progressReport)
    {
        var areasTotal = 0;
        var areasUnique = 0;
        areaEnumerator.EnumerateThroughSprite(areaSizing, spriteIndex, (sprite, index, x, y) =>
        {
            var area = MyArea.CreateFromSprite(sprite, x, y, areaSizing);
            var hash = area.GetHashCode();
            if (areas.TryAdd(hash, area))
                areasUnique++;
            area = areas[hash];

            //var dirtyScore = (long)(Mathf.Pow(area.OpaquePixelsCount, 3f) / area.Dimensions.Square);
            //scores.AddOrUpdate(hash, area.Score, (exisitingKey, existingScore) => existingScore + area.Score);
            area.Correlations.TryAdd(area.Correlations.Count, new MyAreaCoordinates(index, x, y, areaSizing.X, areaSizing.Y));

            areasTotal++;
        });
        progressReport.OperationsDone++;
        return (areasTotal, areasUnique);
    }

    private int getBestArea(IOrderedEnumerable<KeyValuePair<int, long>> rating) => rating.First().Key;

    private async Task<List<MyAreaCoordinates>> applyBestArea(MyColor[][][] sprites, int bestAreaIndex)
    {
        var result = new List<MyAreaCoordinates>();
        MyArea bestArea;
        if (!_allAreas.TryRemove(bestAreaIndex, out bestArea))
            throw new ApplicationException($"Area is not found!");

        var correlations = bestArea.Correlations;
        foreach (var kvp in correlations)
        {
            var myAreaCoordinates = kvp.Value;
            var candidateForErasing = MyArea.CreateFromSprite(sprites[myAreaCoordinates.SpriteIndex], myAreaCoordinates.X, myAreaCoordinates.Y, myAreaCoordinates.Dimensions);
            if (candidateForErasing.GetHashCode() != bestArea.GetHashCode())
                continue;
            MyArea.EraseAreaFromSprite(sprites[myAreaCoordinates.SpriteIndex], myAreaCoordinates.X, myAreaCoordinates.Y, myAreaCoordinates.Dimensions);
            result.Add(myAreaCoordinates);
        }

        //var winnerAreaDimensions = bestArea.Dimensions;

        //await enumerator.EnumerateParallel(bestArea.Dimensions, (sprite, spriteIndex, x, y) =>
        //{
        //    var comparedArea = MyArea.CreateFromSprite(sprite, x, y, winnerAreaDimensions);
        //    if (comparedArea.GetHashCode() == bestArea.GetHashCode())
        //    {
        //        MyArea.EraseAreaFromSprite(sprite, x, y, winnerAreaDimensions);

        //        mappedAreas.Add((spriteIndex, x, y));
        //        result += bestArea.OpaquePixelsCount;
        //    }
        //}, ct);

        //map.Add(bestArea, mappedAreas);

        return result;
    }
}