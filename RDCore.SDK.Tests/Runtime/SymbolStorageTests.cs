using NSubstitute;
using RDCore.SDK.Model.Symbols.Abstract;
using RDCore.SDK.Model.Types;
using RDCore.SDK.Model.Values.Abstract;
using RDCore.SDK.Model.Values.Bindings;
using RDCore.SDK.Runtime.Abstract.Execution;
using RDCore.SDK.Runtime.Shared;
using System.Diagnostics.CodeAnalysis;

namespace RDCore.Tests.Runtime;

[TestClass]
[TestCategory("RD-VBAL §2.3.1.2 References")]
public sealed class SymbolStorageTests
{
    // a storage that reads one binding at one address, and has no read of a place by its type of its own.
    private sealed class OneBinding(MemoryAddress at, IBindingHandle bound) : ISymbolStorage
    {
        public IBindingHandle GetValue(Symbol symbol) => throw new NotSupportedException();

        public bool TryRead(MemoryAddress address, [NotNullWhen(true)][MaybeNullWhen(false)] out IBindingHandle? value)
        {
            value = address == at ? bound : null;
            return value is not null;
        }

        public bool TryGetAddress(Symbol symbol, out MemoryAddress address) => throw new NotSupportedException();

        public bool TryAllocate(Symbol symbol, VBTypedValue value, out MemoryAddress address) => throw new NotSupportedException();
    }

    [TestMethod]
    public void AStorageWithNoReadOfAPlaceByItsType_ReadsWhatIsBoundAtTheAddress_WhateverTheType()
    {
        var bound = Substitute.For<IBindingHandle>();
        ISymbolStorage storage = new OneBinding(new MemoryAddress(64), bound);

        Assert.IsTrue(storage.TryRead(new MemoryAddress(64), VBLongType.TypeInfo, out var typed));
        Assert.IsTrue(storage.TryRead(new MemoryAddress(64), null, out var untyped));
        Assert.IsFalse(storage.TryRead(new MemoryAddress(68), VBLongType.TypeInfo, out _));

        Assert.AreSame(bound, typed);
        Assert.AreSame(bound, untyped);
    }
}
