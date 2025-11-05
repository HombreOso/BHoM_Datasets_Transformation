using System.Text.Json;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var datasetsDir = GetDatasetsDirectory(args);
            var outputDir = GetOutputDirectory(args, datasetsDir);
            if (!Directory.Exists(datasetsDir))
            {
                Console.Error.WriteLine($"Directory not found: {datasetsDir}");
                return 1;
            }

            var jsonFiles = Directory.EnumerateFiles(datasetsDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(p => Path.GetFileName(p))
                .ToList();

            if (jsonFiles.Count == 0)
            {
                Console.WriteLine($"No .json files found in {datasetsDir}");
                return 0;
            }

            Console.WriteLine($"Found {jsonFiles.Count} JSON file(s) in {datasetsDir}\n");
            Directory.CreateDirectory(outputDir);

            var fileNameToContent = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var jsonOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    using var stream = File.OpenRead(filePath);
                    using var doc = JsonDocument.Parse(stream, jsonOptions);
                    object? parsed = ConvertElement(doc.RootElement);
                    fileNameToContent[Path.GetFileName(filePath)] = parsed;

                    // Serialize using BHoM Serializer (if available) and write to output folder
                    string json = SerializeWithBHoMOrFallback(parsed);
                    var outPath = Path.Combine(outputDir, Path.GetFileName(filePath));
                    File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    Console.WriteLine($"Processed {Path.GetFileName(filePath)} -> root: {DescribeType(parsed)} -> saved: {Path.GetFileName(outPath)}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error deserializing {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            // Example: access a specific file's dictionary if its root is an object
            // var example = fileNameToContent["SomeFile.json"] as Dictionary<string, object?>;

            Console.WriteLine("\nDeserialization complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static string GetDatasetsDirectory(string[] args)
    {
        // Usage: dotnet run -- [--dir <path>]
        // Default: ../datasets_original relative to project directory
        var dirFlagIndex = Array.FindIndex(args, a => string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-d", StringComparison.OrdinalIgnoreCase));
        if (dirFlagIndex >= 0 && dirFlagIndex + 1 < args.Length)
        {
            var provided = args[dirFlagIndex + 1];
            return Path.GetFullPath(provided);
        }

        // Try repo-relative default (project is in tools/JsonToDictionary)
        var projectDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(projectDir, "..", "..", "..", "..", "..", "datasets_original"));
        return candidate;
    }

    private static string GetOutputDirectory(string[] args, string inputDirectory)
    {
        // Usage: dotnet run -- [--out <path>]
        var outFlagIndex = Array.FindIndex(args, a => string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase));
        if (outFlagIndex >= 0 && outFlagIndex + 1 < args.Length)
        {
            var provided = args[outFlagIndex + 1];
            return Path.GetFullPath(provided);
        }

        // Default next to the input directory
        var parent = Directory.GetParent(inputDirectory)?.FullName ?? inputDirectory;
        return Path.Combine(parent, "datasets_BHoM");
    }

    private static object? ConvertElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = ConvertElement(property.Value);
                }
                return dict;
            }

            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ConvertElement(item));
                }
                return list;
            }

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
            {
                if (element.TryGetInt64(out var longVal))
                    return longVal;
                if (element.TryGetDouble(out var doubleVal))
                    return doubleVal;
                // Fallback to string to preserve value if extremely large/precise
                return element.ToString();
            }

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return null;
        }
    }

    private static string DescribeType(object? value)
    {
        if (value is null) return "null";
        if (value is Dictionary<string, object?> d) return $"Dictionary<string, object?> (keys: {d.Count})";
        if (value is List<object?> l) return $"List<object?> (count: {l.Count})";
        return value.GetType().Name;
    }

    private static string SerializeWithBHoMOrFallback(object? obj)
    {
        try
        {
            // Try to find BH.Engine.Serialiser.Convert.ToJson via reflection
            var convertType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => string.Equals(t.FullName, "BH.Engine.Serialiser.Convert", StringComparison.Ordinal));

            if (convertType != null)
            {
                var toJson = convertType.GetMethod("ToJson", new[] { typeof(object) });
                if (toJson != null)
                {
                    var result = toJson.Invoke(null, new[] { obj });
                    if (result is string s)
                        return s;
                }
            }
        }
        catch
        {
            // ignore and fallback below
        }

        // Fallback to System.Text.Json if BHoM is unavailable at runtime
        return JsonSerializer.Serialize(obj);
    }
}
