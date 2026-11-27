using RDCore.SDK.Model.Types;
using RDCore.SDK.Model.Values.Abstract;
using RDCore.SDK.Model.Values.Bindings;
using RDCore.SDK.Model.Values.Runtime;
using RDCore.SDK.Runtime.Abstract.Execution;
using System.Buffers.Binary;

namespace RDCore.SDK.Model.Values.Intrinsic;

/// <summary>
/// The storage of one record of a <see cref="VBUserDefinedType"/>, as its <see cref="RecordShape"/> lays it out: the bytes of its fields of fixed-width
/// intrinsic types, the reference slots of its other fields, and whether each field is known. A <see cref="VBUserDefinedTypeValue"/> is a view of a block,
/// at the offsets of the record it is: the record itself, or a record it nests.
/// </summary>
/// <remarks>
/// A field's cell, the binding a <c>ByRef</c> parameter given the field is bound to (<strong>MS-VBAL §5.3.1.11</strong>), and the view of a nested record are
/// made on first need and kept in the block for its life, so that every parameter given one field shares one cell.
/// </remarks>
public sealed class ValueBlock
{
    private object?[]? _made;
    private bool[]? _forgotten;

    /// <summary>
    /// A block for a record of <paramref name="shape"/>, its bytes zero and its slots empty.
    /// </summary>
    /// <param name="shape">The shape of the record.</param>
    public ValueBlock(RecordShape shape)
    {
        Shape = shape;
        Bytes = shape.ByteSize == 0 ? [] : new byte[shape.ByteSize];
        Slots = shape.SlotCount == 0 ? [] : new VBTypedValue[shape.SlotCount];
    }

    /// <summary>
    /// The shape of the record the block holds.
    /// </summary>
    public RecordShape Shape { get; }

    /// <summary>
    /// The bytes of the record's bytes fields, little-endian, each at the offset its shape gives it.
    /// </summary>
    /// <remarks>
    /// A caller that writes these bytes other than through a field's cell calls <see cref="Written"/> afterwards, so that each cell reads what was written.
    /// </remarks>
    public byte[] Bytes { get; }

    /// <summary>
    /// The values of the record's reference fields, each in the slot its shape gives it.
    /// </summary>
    public VBTypedValue[] Slots { get; }

    /// <summary>
    /// How many times the bytes were written other than through a field's cell.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Says the bytes were written other than through a field's cell: each cell reads its field again.
    /// </summary>
    public void Written() => Version++;

    /// <summary>
    /// Whether the field at <paramref name="place"/> is known: a bytes field is, until its cell is told to forget it.
    /// </summary>
    /// <param name="place">The field's place in the block.</param>
    /// <returns><see langword="false"/> when the field's value is not known.</returns>
    public bool IsKnown(int place) => _forgotten is null || !_forgotten[place];

    internal void Know(int place, bool isKnown)
    {
        if (!isKnown)
        {
            (_forgotten ??= new bool[Shape.PlaceCount])[place] = true;
        }
        else if (_forgotten is not null)
        {
            _forgotten[place] = false;
        }
    }

    // what is made of the fields, on first need: a block whose fields are not read one by one makes none of it.
    internal object? MadeAt(int place) => _made?[place];

    internal void Keep(int place, object made) => (_made ??= new object?[Shape.PlaceCount])[place] = made;
}

/// <summary>
/// The cell of a bytes field of a record: what the field holds is the bytes of its <see cref="ValueBlock"/> at the field's offset, which reading the cell
/// decodes and writing it encodes.
/// </summary>
public sealed class BlockBytesCell : IKnowledgeBinding
{
    private IRuntimeValue? _read;
    private int _readVersion;

    internal BlockBytesCell(ValueBlock block, int offset, int place, RecordFieldEncoding encoding)
    {
        Block = block;
        Offset = offset;
        Place = place;
        Encoding = encoding;
    }

    /// <summary>
    /// The block the field's bytes are in.
    /// </summary>
    public ValueBlock Block { get; }

    /// <summary>
    /// The offset of the field's bytes in the block.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// The field's place in the block.
    /// </summary>
    public int Place { get; }

    /// <summary>
    /// How the field's value is encoded.
    /// </summary>
    public RecordFieldEncoding Encoding { get; }

    /// <inheritdoc/>
    public IRuntimeValue Value
    {
        get
        {
            if (_read is null || _readVersion != Block.Version)
            {
                (_read, _readVersion) = (Decode(Block.Bytes.AsSpan(Offset), Encoding), Block.Version);
            }

            return _read;
        }
    }

    /// <inheritdoc/>
    public bool IsKnown => Block.IsKnown(Place);

    /// <inheritdoc/>
    public BindingCapabilities BindingCapabilities => BindingCapabilities.GetValue | BindingCapabilities.SetValue;

    /// <inheritdoc/>
    public IRuntimeValue GetValue(IRuntimeSymbolResolver resolver) => Value;

    /// <inheritdoc/>
    public void SetValue(IRuntimeSymbolResolver resolver, IRuntimeValue value) => Store(value, isKnown: true);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is not a runtime value of the field's type.</exception>
    public void Store(IRuntimeValue value, bool isKnown)
    {
        Encode(value, Block.Bytes.AsSpan(Offset), Encoding);
        (_read, _readVersion) = (value, Block.Version);
        Block.Know(Place, isKnown);
    }

    /// <inheritdoc/>
    public void Forget() => Block.Know(Place, isKnown: false);

    // the field's bytes were written other than through this cell: it decodes them on its next read.
    internal void Reread() => _read = null;

    // the payload of a field's bytes: an integer, a Boolean's -1 or 0 or a Currency's ten-thousandths as a long; a Single or a Double as a double.
    internal static void Read(ReadOnlySpan<byte> bytes, RecordFieldEncoding encoding, out long integral, out double floating)
    {
        (integral, floating) = encoding switch
        {
            RecordFieldEncoding.Byte => ((long)bytes[0], 0d),
            RecordFieldEncoding.Int16 or RecordFieldEncoding.Boolean => ((long)BinaryPrimitives.ReadInt16LittleEndian(bytes), 0d),
            RecordFieldEncoding.Int32 => ((long)BinaryPrimitives.ReadInt32LittleEndian(bytes), 0d),
            RecordFieldEncoding.Int64 or RecordFieldEncoding.Currency => (BinaryPrimitives.ReadInt64LittleEndian(bytes), 0d),
            RecordFieldEncoding.Single => (0L, (double)BinaryPrimitives.ReadSingleLittleEndian(bytes)),
            RecordFieldEncoding.Double => (0L, BinaryPrimitives.ReadDoubleLittleEndian(bytes)),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
    }

    // a payload written to a field's bytes, as Read reads it.
    internal static void Write(Span<byte> bytes, RecordFieldEncoding encoding, long integral, double floating)
    {
        switch (encoding)
        {
            case RecordFieldEncoding.Byte:
                bytes[0] = checked((byte)integral);
                break;
            case RecordFieldEncoding.Int16 or RecordFieldEncoding.Boolean:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, checked((short)integral));
                break;
            case RecordFieldEncoding.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)integral));
                break;
            case RecordFieldEncoding.Int64 or RecordFieldEncoding.Currency:
                BinaryPrimitives.WriteInt64LittleEndian(bytes, integral);
                break;
            case RecordFieldEncoding.Single:
                BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)floating);
                break;
            case RecordFieldEncoding.Double:
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, floating);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }
    }

    /// <inheritdoc/>
    public IRuntimeValue Invoke(IRuntimeSymbolResolver resolver, IRuntimeValue[] args) => throw new NotSupportedException();

    /// <summary>
    /// The runtime value that <paramref name="bytes"/> encode as <paramref name="encoding"/>.
    /// </summary>
    /// <param name="bytes">The field's bytes, from its offset.</param>
    /// <param name="encoding">How the field's value is encoded.</param>
    /// <returns>The field's value.</returns>
    public static IRuntimeValue Decode(ReadOnlySpan<byte> bytes, RecordFieldEncoding encoding) => encoding switch
    {
        RecordFieldEncoding.Byte => new VBRuntimeValue<byte>(bytes[0]),
        RecordFieldEncoding.Int16 => new VBRuntimeValue<short>(BinaryPrimitives.ReadInt16LittleEndian(bytes)),
        RecordFieldEncoding.Boolean => new VBRuntimeBooleanValue(BinaryPrimitives.ReadInt16LittleEndian(bytes)),
        RecordFieldEncoding.Int32 => new VBRuntimeValue<int>(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        RecordFieldEncoding.Int64 => new VBRuntimeValue<long>(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
        RecordFieldEncoding.Single => new VBRuntimeValue<float>(BinaryPrimitives.ReadSingleLittleEndian(bytes)),
        RecordFieldEncoding.Double => new VBRuntimeValue<double>(BinaryPrimitives.ReadDoubleLittleEndian(bytes)),
        RecordFieldEncoding.Currency => new VBRuntimeValue<VBRuntimeCurrencyValue>(new VBRuntimeCurrencyValue(BinaryPrimitives.ReadInt64LittleEndian(bytes))),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="bytes"/>, encoded as <paramref name="encoding"/>.
    /// </summary>
    /// <param name="value">A runtime value of the field's type.</param>
    /// <param name="bytes">The field's bytes, from its offset.</param>
    /// <param name="encoding">How the field's value is encoded.</param>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is not a runtime value of the field's type.</exception>
    public static void Encode(IRuntimeValue value, Span<byte> bytes, RecordFieldEncoding encoding)
    {
        switch (encoding, value)
        {
            case (RecordFieldEncoding.Byte, VBRuntimeValue<byte> number):
                bytes[0] = number.Value;
                break;
            case (RecordFieldEncoding.Int16, VBRuntimeValue<short> number):
                BinaryPrimitives.WriteInt16LittleEndian(bytes, number.Value);
                break;
            case (RecordFieldEncoding.Boolean, VBRuntimeBooleanValue truth):
                BinaryPrimitives.WriteInt16LittleEndian(bytes, truth.StoredValue);
                break;
            case (RecordFieldEncoding.Int32, VBRuntimeValue<int> number):
                BinaryPrimitives.WriteInt32LittleEndian(bytes, number.Value);
                break;
            case (RecordFieldEncoding.Int64, VBRuntimeValue<long> number):
                BinaryPrimitives.WriteInt64LittleEndian(bytes, number.Value);
                break;
            case (RecordFieldEncoding.Single, VBRuntimeValue<float> number):
                BinaryPrimitives.WriteSingleLittleEndian(bytes, number.Value);
                break;
            case (RecordFieldEncoding.Double, VBRuntimeValue<double> number):
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, number.Value);
                break;
            case (RecordFieldEncoding.Currency, VBRuntimeValue<VBRuntimeCurrencyValue> amount):
                BinaryPrimitives.WriteInt64LittleEndian(bytes, amount.Value.StoredValue);
                break;
            default:
                throw new InvalidOperationException($"a {value.GetType().Name} is no {encoding} value of a record field.");
        }
    }
}
