#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.PrototypeTests;

public sealed class PrototypeTests : GameTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ProductionVariantCollectionsPreserveOrderedMembership(bool client)
    {
        RobustIntegrationTest.IntegrationInstance instance = client ? Pair.Client : Pair.Server;
        var prototypes = instance.ResolveDependency<IPrototypeManager>();
        var resources = instance.ResolveDependency<IResourceManager>();
        await instance.WaitAssertion(() =>
        {
            var collections = new List<(string Kind, string File, string[] Members)>();
            foreach (var root in new[] { "/EnginePrototypes/", "/Prototypes/" })
            {
                foreach (var path in resources.ContentFindFiles(root)
                             .Where(path => path.Extension == "yml" && !path.Filename.StartsWith(".")))
                {
                    using var reader = new StreamReader(resources.ContentFileRead(path));
                    collections.AddRange(PrototypeVariantLayoutTest.ReadCollections(reader, path.ToString()));
                }
            }

            Assert.That(collections, Is.Not.Empty);
            Assert.Multiple(() =>
            {
                foreach (var (kind, file, members) in collections)
                {
                    // Follow the native client's ignored kinds; never skip an unregistered server kind.
                    if (client && prototypes.IsIgnored(kind))
                        continue;

                    Assert.That(prototypes.IsIgnored(kind), Is.False, $"{kind} in {file}");
                    switch (kind)
                    {
                        case "entity":
                            AssertVariantCollection<EntityPrototype>(prototypes, members, file);
                            break;
                        case "constructionGraph":
                            AssertVariantCollection<ConstructionGraphPrototype>(prototypes, members, file);
                            break;
                        case "construction":
                            AssertVariantCollection<ConstructionPrototype>(prototypes, members, file);
                            break;
                        default:
                            Assert.Fail($"Add typed variant-membership coverage for {kind} in {file}.");
                            break;
                    }
                }
            });
        });
    }

    private static void AssertVariantCollection<T>(IPrototypeManager prototypes, string[] members, string file)
        where T : class, IPrototype
    {
        Assert.That(prototypes.TryGetKindFrom<T>(out _), Is.True, $"{typeof(T).Name} in {file}");
        var expected = members.Select(member => new ProtoId<T>(member)).ToArray();
        foreach (var member in expected)
        {
            var message = $"{typeof(T).Name} {member} in {file}";
            Assert.That(prototypes.HasIndex(member), Is.True, message);
            Assert.That(prototypes.TryGetVariantCollection(member, out var actual), Is.True, message);
            Assert.That(actual, Is.EqualTo(expected), message);
        }
    }

    /// <summary>
    /// This test writes all known server prototypes as yaml files, then validates that the result is valid yaml.
    /// Can help prevent instances where prototypes have bad C# default values.
    /// </summary>
    [Test]
    public async Task TestAllServerPrototypesAreSerializable()
    {
        var ser = Pair.Server.ResolveDependency<ISerializationManager>();
        var context = new PrototypeSaveTest.TestEntityUidContext(ser);
        await SaveThenValidatePrototype(Pair.Server, "server", context);
    }

    /// <summary>
    /// This test writes all known client prototypes as yaml files, then validates that the result is valid yaml.
    /// Can help prevent instances where prototypes have bad C# default values.
    /// </summary>
    [Test]
    public async Task TestAllClientPrototypesAreSerializable()
    {
        var ser = Pair.Server.ResolveDependency<ISerializationManager>();
        var context = new PrototypeSaveTest.TestEntityUidContext(ser);
        await SaveThenValidatePrototype(Pair.Client, "client", context);
    }

    public async Task SaveThenValidatePrototype(RobustIntegrationTest.IntegrationInstance instance, string instanceId,
        PrototypeSaveTest.TestEntityUidContext ctx)
    {
        var protoMan = instance.ResolveDependency<IPrototypeManager>();
        Dictionary<Type, Dictionary<string, HashSet<ErrorNode>>> errors = default!;
        await instance.WaitPost(() => errors = protoMan.ValidateAllPrototypesSerializable(ctx));

        if (errors.Count == 0)
            return;

        Assert.Multiple(() =>
        {
            foreach (var (kind, ids) in errors)
            {
                foreach (var (id, nodes) in ids)
                {
                    var msg = $"Error when validating {instanceId} prototype ({kind.Name}, {id}). Errors: \n";
                    foreach (var errorNode in nodes)
                    {
                        msg += $" - {errorNode.ErrorReason}\n";
                    }
                    Assert.Fail(msg);
                }
            }
        });
    }

    /// <summary>
    /// This test writes all known prototypes as yaml files, reads them again, then serializes them again.
    /// </summary>
    [Test]
    public async Task ServerPrototypeSaveLoadSaveTest()
    {
        var ser = Pair.Server.ResolveDependency<ISerializationManager>();
        var context = new PrototypeSaveTest.TestEntityUidContext(ser);
        await SaveLoadSavePrototype(Pair.Server, context);
    }

    /// <summary>
    /// This test writes all known prototypes as yaml files, reads them again, then serializes them again.
    /// </summary>
    [Test]
    public async Task ClientPrototypeSaveLoadSaveTest()
    {
        var ser = Pair.Server.ResolveDependency<ISerializationManager>();
        var context = new PrototypeSaveTest.TestEntityUidContext(ser);
        await SaveLoadSavePrototype(Pair.Client, context);
    }

    private async Task SaveLoadSavePrototype(
        RobustIntegrationTest.IntegrationInstance instance,
        PrototypeSaveTest.TestEntityUidContext ctx)
    {
        var protoMan = instance.ResolveDependency<IPrototypeManager>();
        var seriMan = instance.ResolveDependency<ISerializationManager>();
        await instance.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var kind in protoMan.EnumeratePrototypeKinds())
                {
                    foreach (var proto in protoMan.EnumeratePrototypes(kind))
                    {
                        var noException = TrySaveLoadSavePrototype(
                            seriMan,
                            protoMan,
                            kind,
                            proto,
                            ctx);

                        // This will probably throw an exception for each prototype of this kind.
                        // We want to avoid having tests crash because they run out of time.
                        if (!noException)
                            break;
                    }
                }
            });
        });
    }

    /// <returns>False if an exception was caught</returns>
    private bool TrySaveLoadSavePrototype(
        ISerializationManager seriMan,
        IPrototypeManager protoMan,
        Type kind,
        IPrototype proto,
        PrototypeSaveTest.TestEntityUidContext ctx)
    {
        DataNode first;
        DataNode second;

        try
        {
            first = seriMan.WriteValue(kind, proto, alwaysWrite: true, context:ctx);
        }
        catch (Exception e)
        {
            protoMan.TryGetMapping(kind, proto.ID, out var mapping);
            Assert.Fail($"Caught exception while writing {kind.Name} prototype {proto.ID}. Exception:\n{e}");
            return false;
        }

        object? obj;
        try
        {
            obj = seriMan.Read(kind, first, context:ctx);
        }
        catch (Exception e)
        {
            protoMan.TryGetMapping(kind, proto.ID, out var mapping);
            Assert.Fail($"Caught exception while re-reading {kind.Name} prototype {proto.ID}." +
                        $"\nException:\n{e}" +
                        $"\n\nOriginal yaml:\n{mapping}" +
                        $"\n\nWritten yaml:\n{first}");
            return false;
        }

        Assert.That(obj?.GetType(), Is.EqualTo(proto.GetType()));
        var deserialized = (IPrototype) obj!;

        try
        {
            second = seriMan.WriteValue(kind, deserialized, alwaysWrite: true, context:ctx);
        }
        catch (Exception e)
        {
            protoMan.TryGetMapping(kind, proto.ID, out var mapping);
            Assert.Fail($"Caught exception while re-writing {kind.Name} prototype {proto.ID}." +
                        $"\nException:\n{e}" +
                        $"\n\nOriginal yaml:\n{mapping}" +
                        $"\n\nWritten yaml:\n{first}");
            return false;
        }

        var diff = first.Except(second);
        if (diff == null || diff.IsEmpty)
            return true;

        protoMan.TryGetMapping(kind, proto.ID, out var orig);
        Assert.Fail($"Re-written {kind.Name} prototype {proto.ID} differs." +
                    $"\nYaml diff:\n{diff}" +
                    $"\n\nOriginal yaml:\n{orig}" +
                    $"\n\nWritten yaml:\n{first}" +
                    $"\n\nRe-written Yaml:\n{second}");
        return true;
    }
}
