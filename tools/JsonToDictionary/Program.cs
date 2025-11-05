using System.Text.Json;
using System.Text;
using BH.Engine;
using BH.oM.PlantRoomSizer;

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
                    var series = ConvertToDataSeries(doc.RootElement);
                    if (series is null || series.DataPoints == null || series.DataPoints.Count == 0)
                    {
                        Console.Error.WriteLine($"{Path.GetFileName(filePath)} does not contain a valid DataSeries (expected array of points or object with 'Data' array).");
                        continue;
                    }
                    fileNameToContent[Path.GetFileName(filePath)] = series;

                    // Serialize using BHoM Serializer only and write to output folder
                    if (!TrySerializeWithBHoM(series, out var json, out var serializeError))
                    {
                        Console.Error.WriteLine($"BHoM serialization failed for {Path.GetFileName(filePath)}: {serializeError}");
                        continue;
                    }
                    var outPath = Path.Combine(outputDir, Path.GetFileName(filePath));
                    File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    Console.WriteLine($"Processed {Path.GetFileName(filePath)} -> root: {DescribeType(series)} -> saved: {Path.GetFileName(outPath)}");
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
        if (value is DataSeries ds && ds.DataPoints != null) return $"DataSeries (points: {ds.DataPoints.Count})";
        return value.GetType().Name;
    }

    private static CurvePoint? ConvertToCurvePoint(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                double? x = null;
                double? y = null;
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "x", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "X", StringComparison.Ordinal))
                    {
                        if (TryGetDouble(property.Value, out var dx)) x = dx;
                    }
                    else if (string.Equals(property.Name, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "Y", StringComparison.Ordinal))
                    {
                        if (TryGetDouble(property.Value, out var dy)) y = dy;
                    }
                }
                if (x.HasValue && y.HasValue)
                    return new CurvePoint { X = x.Value, Y = y.Value };
                return null;
            }

            case JsonValueKind.Array:
            {
                var enumerated = element.EnumerateArray().ToList();
                if (enumerated.Count == 2 && TryGetDouble(enumerated[0], out var ax) && TryGetDouble(enumerated[1], out var ay))
                    return new CurvePoint { X = ax, Y = ay };
                return null;
            }

            default:
                return null;
        }
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);
            case JsonValueKind.String:
            {
                var s = element.GetString();
                return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
                    || double.TryParse(s, out value);
            }
            default:
                value = default;
                return false;
        }
    }

    private static DataSeries? ConvertToDataSeries(JsonElement root)
    {
        // Accept either a root array of points, or an object with a Data array
        if (root.ValueKind == JsonValueKind.Array)
        {
            var list = new List<CurvePoint>();
            foreach (var item in root.EnumerateArray())
            {
                var p = ConvertToCurvePoint(item);
                if (p != null) list.Add(p);
            }
            return list.Count > 0 ? new DataSeries { DataPoints = list } : null;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            // Prefer a "Data" array if present
            if (root.TryGetProperty("Data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<CurvePoint>();
                foreach (var item in dataElement.EnumerateArray())
                {
                    var p = ConvertToCurvePoint(item);
                    if (p != null) list.Add(p);
                }
                return list.Count > 0 ? new DataSeries { DataPoints = list } : null;
            }

            // If not a collection, try to parse the object itself as a single point
            var single = ConvertToCurvePoint(root);
            if (single != null) return new DataSeries { DataPoints = new List<CurvePoint> { single } };
        }

        return null;
    }

    private static bool TrySerializeWithBHoM(object? obj, out string json, out string error)
    {
        try
        {
            json = BH.Engine.Serialiser.Convert.ToJson(obj);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            json = string.Empty;
            error = ex.Message;
            return false;
        }
    }
}
