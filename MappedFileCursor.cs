using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace FixDebugInfo;

class MappedFileCursor(MemoryMappedViewAccessor view, long cursorBase, long end, uint offset)
{
	#region Constructors

	public MappedFileCursor(MemoryMappedViewAccessor view, long cursorBase, uint size)
		: this(view, cursorBase, cursorBase + size, 0) {}

	public MappedFileCursor(MemoryMappedViewAccessor view, long cursorBase)
		: this(view, cursorBase, 0, 0) {}

	public MappedFileCursor(MappedFileCursor cursor, uint size)
		: this(cursor.View, cursor.Base, cursor.Base + size, 0) {}

	public MappedFileCursor(MappedFileCursor cursor)
		: this(cursor.View, cursor.Base, cursor.End, cursor.Offset) {}

	public MappedFileCursor(MemoryMappedViewAccessor view)
		: this(view, 0, 0, 0) {}

	#endregion


	#region Properties

	public MemoryMappedViewAccessor View { get; } = view;
	public long Base { get; } = cursorBase;
	public long End { get; set; } = end;
	public uint Offset { get; set; } = offset;
	public long Next 
	{ 
		get => Base + Offset;
		set => Offset = (uint)(value - Base); 
	}
	public bool AtEnd => Next >= End;

	#endregion


	#region Public Methods

	public void Read<T>(out T res) where T : struct
	{
		View.Read(Next, out res);
		Next += (uint)Marshal.SizeOf<T>();
	}

	public byte ReadByte()
	{
		byte b = View.ReadByte(Next);
		Next += sizeof(byte);
		return b;
	}

	public ushort ReadUshort()
	{
		ushort us = View.ReadUInt16(Next);
		Next += sizeof(ushort);
		return us;
	}

	public uint ReadUint()
	{
		uint u = View.ReadUInt32(Next);
		Next += sizeof(uint);
		return u;
	}

	public string ReadString()
	{
		StringBuilder stb;
		char ch;

		stb = new StringBuilder();
		for (; ; )
		{
			ch = (char)View.ReadByte(Next++);
			if (ch == 0)
				break;
			stb.Append(ch);
		}
		return stb.ToString();
	}

	public string ReadString(uint off)
	{
		Offset = off;
		return ReadString();
	}

	public void Write<T>(ref T res) where T : struct
	{
		View.Write(Next, ref res);
		Next += (uint)Marshal.SizeOf<T>();
	}

	public void WriteByte(byte b)
	{
		View.Write(Next, b);
		Next += sizeof(byte);
	}

	public void WriteUshort(ushort us)
	{
		View.Write(Next, us);
		Next += sizeof(ushort);
	}

	public void WriteUint(uint u)
	{
		View.Write(Next, u);
		Next += sizeof(uint);
	}

	public void WriteByteArray(byte[] arb)
	{
		View.WriteArray(Next, arb, 0, arb.Length);
		Next += (uint)arb.Length;
	}

	#endregion
}
