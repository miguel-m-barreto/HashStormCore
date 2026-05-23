using System.Text.Json;

namespace HashStormCore.Payouts.CoinMetadata;

public class CoinsJsonCoinMetadataRegistry : ICoinMetadataRegistry
{
    public CoinsJsonCoinMetadataRegistry(string coinsJsonPath)
    {
        if(string.IsNullOrWhiteSpace(coinsJsonPath))
            throw new ArgumentException("coins.json path is required", nameof(coinsJsonPath));

        this.coinsJsonPath = coinsJsonPath;
    }

    private readonly string coinsJsonPath;
    private readonly object sync = new();
    private IReadOnlyDictionary<string, CoinDescriptor> coins;

    public bool TryGetCoin(string coinKey, out CoinDescriptor descriptor)
    {
        if(string.IsNullOrWhiteSpace(coinKey))
        {
            descriptor = null!;
            return false;
        }

        return GetCoins().TryGetValue(coinKey.Trim(), out descriptor!);
    }

    private IReadOnlyDictionary<string, CoinDescriptor> GetCoins()
    {
        if(coins != null)
            return coins;

        lock(sync)
        {
            coins ??= LoadCoins();
            return coins;
        }
    }

    private IReadOnlyDictionary<string, CoinDescriptor> LoadCoins()
    {
        if(!File.Exists(coinsJsonPath))
            throw new FileNotFoundException($"coins.json metadata file was not found at '{coinsJsonPath}'", coinsJsonPath);

        using var stream = File.OpenRead(coinsJsonPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if(document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("coins.json root must be a JSON object");

        var result = new Dictionary<string, CoinDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach(var coin in document.RootElement.EnumerateObject())
        {
            if(coin.Value.ValueKind != JsonValueKind.Object)
                continue;

            var usesZCashAddressFormat = GetBool(coin.Value, "usesZCashAddressFormat");
            var descriptor = new CoinDescriptor
            {
                CoinKey = coin.Name,
                Name = GetString(coin.Value, "name"),
                CanonicalName = GetString(coin.Value, "canonicalName"),
                Symbol = GetString(coin.Value, "symbol"),
                Family = GetString(coin.Value, "family"),
                HasBrokenSendMany = GetBool(coin.Value, "hasBrokenSendMany") ?? false,
                UseBitcoinPayoutHandler = GetBool(coin.Value, "useBitcoinPayoutHandler") ?? false,
                UsesZCashAddressFormat = usesZCashAddressFormat ?? false,
                HasUsesZCashAddressFormat = usesZCashAddressFormat.HasValue,
                PayoutDecimalPlaces = GetInt(coin.Value, "payoutDecimalPlaces"),
                RawExtensionFlags = ReadScalarFlags(coin.Value)
            };

            result[coin.Name] = descriptor;
        }

        return result;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if(!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static IReadOnlyDictionary<string, string> ReadScalarFlags(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach(var property in element.EnumerateObject())
        {
            switch(property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Number:
                    result[property.Name] = property.Value.GetRawText();
                    break;
            }
        }

        return result;
    }
}
