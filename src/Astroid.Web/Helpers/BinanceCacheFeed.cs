using Astroid.Core;
using Astroid.Providers;
using Binance.Net.Clients;
using Binance.Net.Objects;
using Binance.Net.Objects.Models.Futures;

namespace Astroid.Web;

public class BinanceCacheFeed : IDisposable
{
	private BinanceSocketClient SocketClient { get; set; }

	private BinanceClient Client { get; set; }

	public BinanceCacheFeed()
	{
		var key = Environment.GetEnvironmentVariable("ASTROID_BINANCE_TEST_KEY");
		var secret = Environment.GetEnvironmentVariable("ASTROID_BINANCE_TEST_SECRET");

		if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
			throw new Exception("Binance credentials not found.");

		var creds = new BinanceApiCredentials(key, secret);

		SocketClient = new BinanceSocketClient(new BinanceSocketClientOptions
		{
			UsdFuturesStreamsOptions = new BinanceSocketApiClientOptions
			{
				BaseAddress = "wss://stream.binancefuture.com",
				ApiCredentials = creds,
			},
			LogLevel = LogLevel.Debug
		});

		Client = new BinanceClient(new BinanceClientOptions
		{
			UsdFuturesApiOptions = new BinanceApiClientOptions
			{
				BaseAddress = "https://testnet.binancefuture.com",
				ApiCredentials = creds,
			},
			LogLevel = LogLevel.Debug
		});
	}

	public async Task StartSubscriptions()
	{
		await GetExchangeInfo();
		await SocketClient.UsdFuturesStreams.SubscribeToAllTickerUpdatesAsync(data =>
		{
			var markPrices = data.Data;

			foreach (var priceInfo in markPrices)
			{
				var symbolInfo = ExchangeInfoStore.GetSymbolInfo(ACExchanges.BinanceUsdFutures, priceInfo.Symbol);
				if (symbolInfo == null) continue;

				symbolInfo.LastPrice = priceInfo.LastPrice;
				symbolInfo.ModifiedAt = DateTime.UtcNow;
			}
		});

		var binanceInfo = ExchangeInfoStore.Get(ACExchanges.BinanceUsdFutures);
		var ticker = binanceInfo!.Symbols.FirstOrDefault(x => x.Name == "BTCUSDT");
		await SocketClient.UsdFuturesStreams.SubscribeToOrderBookUpdatesAsync(ticker.Name, 500, data =>
		{
			ticker.OrderBook.ProcessUpdate(data.Data);
			if (ticker.OrderBook.LastUpdateTime == 0)
			{
				ticker.OrderBook.LastUpdateTime = -1;
				Console.WriteLine("Getting snapshot");
				GetDepthSnapshot(ticker.OrderBook);
			}

			// var bestBid = ticker.OrderBook.GetBestBid();
			// var bestAsk = ticker.OrderBook.GetBestAsk();
			// Console.WriteLine($"Binance {ticker.Name} {bestBid} {bestAsk}");
		});

		// foreach (var ticker in binanceInfo!.Symbols)
		// {
		// 	await SocketClient.UsdFuturesStreams.SubscribeToOrderBookUpdatesAsync(ticker.Name, 500, data =>
		// 	{
		// 		ticker.OrderBook.ProcessUpdate(data.Data);
		// 		if (ticker.OrderBook.LastUpdateTime == 0)
		// 		{
		// 			ticker.OrderBook.LastUpdateTime = -1;
		// 			Console.WriteLine("Getting snapshot");
		// 			GetDepthSnapshot(ticker.OrderBook);
		// 		}

		// 		var bestBid = ticker.OrderBook.GetFirstBid();
		// 		var bestAsk = ticker.OrderBook.GetFirstAsk();
		// 		Console.WriteLine($"Binance {ticker.Name} {bestBid} {bestAsk}");
		// 	});
		// }
	}

	public async Task StopSubscriptions() => await SocketClient.UnsubscribeAllAsync();

	public async Task GetExchangeInfo()
	{
		var info = await Client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
		var exchangeInfo = new AMExchangeInfo
		{
			Name = "Binance USD Futures",
			ModifiedAt = DateTime.UtcNow,
			Symbols = info.Data.Symbols.Where(x => x.Name == "BTCUSDT").Select(x =>
			{
				var lastPrice = Client.UsdFuturesApi.ExchangeData.GetPriceAsync(x.Name).GetAwaiter().GetResult().Data.Price;
				return new AMSymbolInfo
				{
					Name = x.Name,
					QuantityPrecision = x.QuantityPrecision,
					PricePrecision = x.PricePrecision,
					TickSize = x.LotSizeFilter?.StepSize,
					LastPrice = lastPrice,
					ModifiedAt = DateTime.UtcNow,
					OrderBook = new AMOrderBook(x.Name)
				};
			}).ToList()
		};

		ExchangeInfoStore.Add(ACExchanges.BinanceUsdFutures, exchangeInfo);
	}

	public async void GetDepthSnapshot(AMOrderBook orderBook)
	{
		var snapshot = await Client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(orderBook.Symbol, 1000);
		orderBook.LoadSnapshot(snapshot.Data.Asks, snapshot.Data.Bids, snapshot.Data.LastUpdateId);
	}

	public async Task<BinanceFuturesOrderBook> GetDepth(string ticker)
	{
		var snapshot = await Client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(ticker, 1000);
		return snapshot.Data;
	}

	public void Dispose()
	{
		SocketClient?.UnsubscribeAllAsync().Wait();
		SocketClient?.Dispose();
		Client?.Dispose();
	}
}
