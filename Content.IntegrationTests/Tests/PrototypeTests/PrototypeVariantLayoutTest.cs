#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.IntegrationTests.Utility;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.IntegrationTests.Tests.PrototypeTests;

[TestFixture]
public sealed class PrototypeVariantLayoutTest
{
    [Test]
    public void VariantDeclarationsHaveSingleFilePerKind()
    {
        // No GameTest/pool boot: the unsafe load can fail before a runtime assertion is reached.
        var roots = new[]
        {
            GameDataScrounger.GetContentPathOnDisk("/Prototypes"),
            Path.GetFullPath(Path.Combine(GameDataScrounger.GetContentPathOnDisk("/"),
                "..", "RobustToolbox", "Resources", "EnginePrototypes")),
        };
        var collections = new List<(string Kind, string File, string[] Members)>();
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories)
                         .Where(file => !Path.GetFileName(file).StartsWith(".")))
            {
                using var reader = File.OpenText(file);
                collections.AddRange(ReadCollections(reader, file));
            }
        }

        Assert.That(collections, Is.Not.Empty);
        // ponytail: one file per kind bounds native LoadDirectory's per-file PLINQ writers.
        // This does not serialize concurrent explicit loads/reloads or change engine synchronization.
        Assert.Multiple(() =>
        {
            foreach (var kind in collections.GroupBy(collection => collection.Kind))
            {
                var files = kind.Select(collection => collection.File).Distinct().Order().ToArray();
                Assert.That(files.Length, Is.LessThanOrEqualTo(1),
                    $"{kind.Key}: {kind.Count()} variant collections across {files.Length} files. " +
                    $"Keep ID !type:CreateVariants declarations in one file per kind:\n{string.Join("\n", files)}");
            }
        });
    }

    internal static IEnumerable<(string Kind, string File, string[] Members)> ReadCollections(
        TextReader reader, string file)
    {
        foreach (var document in DataNodeParser.ParseYamlStream(reader))
        {
            if (document.Root is ValueDataNode { Value: "" })
                continue;

            foreach (var node in ((SequenceDataNode) document.Root).Sequence)
            {
                var mapping = (MappingDataNode) node;
                if (!mapping.TryGet<MappingDataNode>(IdDataFieldAttribute.Name, out var id) ||
                    id.Tag != "!type:CreateVariants")
                    continue;

                var kind = mapping.Get<ValueDataNode>("type").Value;
                var members = id.Get<SequenceDataNode>(VariantValuesFieldAttribute.Name).Sequence
                    .Select(member => ((ValueDataNode) member).Value).ToArray();
                Assert.That(members, Is.Not.Empty, $"{kind} in {file}");
                Assert.That(members.All(member => !string.IsNullOrWhiteSpace(member)), Is.True,
                    $"Blank variant ID in {file}");
                Assert.That(members, Is.Unique, $"Duplicate variant ID in {file}");
                yield return (kind, file, members);
            }
        }
    }
}
