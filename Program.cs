using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
á¸
class Program
{
    static readonly HttpClient http = new HttpClient();

    const string symbol = "BTCUSDT";
    const string interval = "1h";
    const int atrPeriod = 10;
    const int emaPeriod = 20;

    static async Task Main()
    {
        try
        {
            var candles = await GetHistoricalCandles(symbol, interval, totalCandles: 5000);

            Console.WriteLine($"Fetched {candles.Count} candles for {symbol}.");

            var emaValues = CalculateEma(candles, emaPeriod);
            var atrValues = CalculateExponentialAtr(candles, atrPeriod);

            Console.WriteLine("Starting backtest...");
            Backtest(candles, emaValues, atrValues);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
        }
    }

    record Candle(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close);

    static async Task<List<Candle>> GetCandlesAsync(string symbol, string interval, int limit = 1000, long? startTime = null, long? endTime = null)
    {
        string url = $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
        if (startTime.HasValue) url += $"&startTime={startTime.Value}";
        if (endTime.HasValue) url += $"&endTime={endTime.Value}";

        var response = await http.GetStringAsync(url);
        var jsonDoc = JsonDocument.Parse(response);
        var candles = new List<Candle>();

        foreach (var element in jsonDoc.RootElement.EnumerateArray())
        {
            long openTimeMs = element[0].GetInt64();
            decimal open = decimal.Parse(element[1].GetString());
            decimal high = decimal.Parse(element[2].GetString());
            decimal low = decimal.Parse(element[3].GetString());
            decimal close = decimal.Parse(element[4].GetString());

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime,
                open, high, low, close));
        }

        return candles;
    }

    static async Task<List<Candle>> GetHistoricalCandles(string symbol, string interval, int totalCandles)
    {
        var allCandles = new List<Candle>();
        int limit = 1000;
        long? endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (allCandles.Count < totalCandles)
        {
            var candles = await GetCandlesAsync(symbol, interval, limit, null, endTime);
            if (candles.Count == 0) break;

            allCandles.InsertRange(0, candles);

            if (candles.Count < limit) break;

            endTime = new DateTimeOffset(candles[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            await Task.Delay(500);
        }

        if (allCandles.Count > totalCandles)
            allCandles.RemoveRange(0, allCandles.Count - totalCandles);

        return allCandles;
    }

    static List<decimal> CalculateEma(List<Candle> candles, int period)
    {
        var ema = new List<decimal>();
        decimal multiplier = 2m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < candles.Count; i++)
        {
            var close = candles[i].Close;
            if (i == 0)
            {
                ema.Add(close);
                prevEma = close;
            }
            else
            {
                var currentEma = (close - prevEma.Value) * multiplier + prevEma.Value;
                ema.Add(currentEma);
                prevEma = currentEma;
            }
        }

        return ema;
    }

    static List<decimal> CalculateExponentialAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();

        for (int i = 1; i < candles.Count; i++)
        {
            var current = candles[i];
            var prev = candles[i - 1];

            var highLow = current.High - current.Low;
            var highClose = Math.Abs(current.High - prev.Close);
            var lowClose = Math.Abs(current.Low - prev.Close);

            trs.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        var atr = new List<decimal>();
        decimal multiplier = 2m / (period + 1);
        decimal? prevAtr = null;

        for (int i = 0; i < trs.Count; i++)
        {
            if (i < period)
            {
                atr.Add(0);
            }
            else if (i == period)
            {
                decimal sum = 0;
                for (int j = i - period; j <= i; j++)
                    sum += trs[j];
                prevAtr = sum / period;
                atr.Add(prevAtr.Value);
            }
            else
            {
                var currentAtr = (trs[i] - prevAtr.Value) * multiplier + prevAtr.Value;
                atr.Add(currentAtr);
                prevAtr = currentAtr;
            }
        }

        atr.Insert(0, 0); // align with candles count
        return atr;
    }

    static void Backtest(List<Candle> candles, List<decimal> emaValues, List<decimal> atrValues)
    {
        List<Trade> trades = new();
        Trade? currentTrade = null;
        decimal atrMultiplier = 1.5m;

        decimal equity = 1000m;
        decimal riskPercent = 0.02m;

        decimal minAtrThreshold = 100m;
        decimal breakoutMultiplier = 1.5m;

        for (int i = 1; i < candles.Count; i++)
        {
            var candle = candles[i];
            var prevCandle = candles[i - 1];
            var ema = emaValues[i];
            var atr = atrValues[i];

            if (atr == 0) continue;

            // Manage open trade
            if (currentTrade != null && currentTrade.IsOpen)
            {
                decimal stopLoss = currentTrade.EntryPrice - atrMultiplier * atr;

                if (candle.Low <= stopLoss)
                {
                    currentTrade.ExitPrice = stopLoss;
                    currentTrade.ExitTime = candle.OpenTime;
                    trades.Add(currentTrade);

                    decimal pl = (currentTrade.ExitPrice.Value - currentTrade.EntryPrice) * currentTrade.PositionSize;
                    equity += pl;
                    currentTrade = null;
                    continue;
                }
            }

            // Entry logic
            if (currentTrade == null && atr >= minAtrThreshold)
            {
                if (candle.Close > ema)
                {
                    var breakoutLevel = prevCandle.High + breakoutMultiplier * atr;
                    if (candle.Close > breakoutLevel)
                    {
                        decimal riskAmount = equity * riskPercent;
                        decimal stopLossDistance = atrMultiplier * atr;
                        decimal positionSize = riskAmount / stopLossDistance;

                        currentTrade = new Trade
                        {
                            EntryTime = candle.OpenTime,
                            EntryPrice = candle.Close,
                            PositionSize = positionSize,
                            Type = PositionType.Long
                        };
                    }
                }
            }
        }

        // Exit last trade at end
        if (currentTrade != null && currentTrade.IsOpen)
        {
            currentTrade.ExitPrice = candles[^1].Close;
            currentTrade.ExitTime = candles[^1].OpenTime;
            trades.Add(currentTrade);

            decimal pl = (currentTrade.ExitPrice.Value - currentTrade.EntryPrice) * currentTrade.PositionSize;
            equity += pl;
        }

        // Summary
        decimal totalProfit = equity - 1000m;
        int wins = 0;

        foreach (var t in trades)
        {
            decimal pl = (t.ExitPrice.Value - t.EntryPrice) * t.PositionSize;
            if (pl > 0) wins++;
            Console.WriteLine($"Trade: Entry {t.EntryTime}, Exit {t.ExitTime}, Size: {t.PositionSize:F4}, P/L: {pl:F2}");
        }

        int totalTrades = trades.Count;
        decimal winRate = totalTrades > 0 ? (decimal)wins / totalTrades * 100 : 0;

        Console.WriteLine($"\n--- Performance Summary ---");
        Console.WriteLine($"Starting Capital: €1000.00");
        Console.WriteLine($"Ending Capital: €{equity:F2}");
        Console.WriteLine($"Total Trades: {totalTrades}, Wins: {wins}, Win Rate: {winRate:F2}%");
        Console.WriteLine($"Total Profit: €{totalProfit:F2}");
    }

    public enum PositionType { Long }

    class Trade
    {
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime? ExitTime { get; set; }
        public decimal? ExitPrice { get; set; }
        public decimal PositionSize { get; set; }
        public PositionType Type { get; set; }
        public bool IsOpen => ExitPrice == null;
    }
}
