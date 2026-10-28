using System.Text.Json;

namespace QuizMMO.Server.Gameplay.Data;

/// <summary>아이템 정의 하나. Data/items.json 의 항목과 1:1.</summary>
public sealed class ItemDefinition
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>false 면 거래 제안에 올릴 수 없다 (참가 배지 등).</summary>
    public bool Tradable { get; init; } = true;
}

/// <summary>아이템 정의 사전. 시작 시 JSON 을 읽는다. 로드 후 읽기만 하므로 어느 스레드에서든 안전.</summary>
public sealed class ItemCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<uint, ItemDefinition> _items;

    private ItemCatalog(Dictionary<uint, ItemDefinition> items)
    {
        _items = items;
    }

    public int Count => _items.Count;

    public static ItemCatalog Empty() => new(new Dictionary<uint, ItemDefinition>());

    public static ItemCatalog LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"item file not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    public static ItemCatalog Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ItemFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("item file is empty");
        var items = new Dictionary<uint, ItemDefinition>();
        foreach (var item in file.Items)
        {
            if (item.Id == 0) throw new InvalidDataException("item id 0 is reserved");
            if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException($"item {item.Id}: empty name");
            if (!items.TryAdd(item.Id, item)) throw new InvalidDataException($"item {item.Id}: duplicate id");
        }
        return new ItemCatalog(items);
    }

    /// <summary>테스트용: 정의 목록으로 바로 만든다.</summary>
    public static ItemCatalog FromItems(IEnumerable<ItemDefinition> items) => new(items.ToDictionary(i => i.Id));

    public bool Contains(uint itemId) => _items.ContainsKey(itemId);
    public bool TryGet(uint itemId, out ItemDefinition item) => _items.TryGetValue(itemId, out item!);
    public bool IsTradable(uint itemId) => _items.TryGetValue(itemId, out var item) && item.Tradable;

    private sealed class ItemFile
    {
        public List<ItemDefinition> Items { get; set; } = new();
    }
}
