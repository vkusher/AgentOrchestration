using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Loads <see cref="AgentMeshOptions"/> from YAML files.
/// </summary>
public static class AgentMeshYamlLoader
{
    /// <summary>
    /// Deserialize YAML content into AgentMeshOptions.
    /// </summary>
    public static AgentMeshOptions LoadFromYamlContent(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            throw new ArgumentException("YAML content is empty.", nameof(yamlContent));

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();

        var wrapper = deserializer.Deserialize<YamlWrapper>(yamlContent);
        if (wrapper?.AgentMesh is null)
            throw new InvalidOperationException("Failed to deserialize YAML 'AgentMesh' section to AgentMeshOptions.");

        return wrapper.AgentMesh;
    }

    /// <summary>
    /// Load AgentMeshOptions from a YAML file.
    /// </summary>
    public static AgentMeshOptions LoadFromYamlFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"YAML file not found: {filePath}", filePath);

        var content = File.ReadAllText(filePath);
        return LoadFromYamlContent(content);
    }

    private sealed class YamlWrapper
    {
        public AgentMeshOptions? AgentMesh { get; set; }
    }

    /// <summary>
    /// Serialize an <see cref="AgentMeshOptions"/> back to YAML text using the same
    /// PascalCase shape understood by <see cref="LoadFromYamlContent"/>. Multi-line
    /// strings (e.g. instructions) are emitted as literal block scalars (|) for
    /// readability.
    /// </summary>
    public static string SaveToYamlContent(AgentMeshOptions mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithEventEmitter(next => new MultilineLiteralEmitter(next))
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .DisableAliases()
            .Build();

        var wrapper = new YamlWrapper { AgentMesh = mesh };
        return serializer.Serialize(wrapper);
    }

    /// <summary>
    /// Write an <see cref="AgentMeshOptions"/> to the given YAML file path (atomically).
    /// </summary>
    public static void SaveToYamlFile(string filePath, AgentMeshOptions mesh)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is empty.", nameof(filePath));

        var content = SaveToYamlContent(mesh);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(filePath))
            File.Replace(tmp, filePath, destinationBackupFileName: null);
        else
            File.Move(tmp, filePath);
    }

    private sealed class MultilineLiteralEmitter : ChainedEventEmitter
    {
        public MultilineLiteralEmitter(IEventEmitter next) : base(next) { }

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string) && eventInfo.Source.Value is string s && s.Contains('\n'))
            {
                eventInfo.Style = ScalarStyle.Literal;
            }
            base.Emit(eventInfo, emitter);
        }
    }
}


