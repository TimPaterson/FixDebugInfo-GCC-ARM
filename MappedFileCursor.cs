using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace FixDebugInfo
{
	class MappedFileCursor
	{
		#region Constructors

		public MappedFileCursor(MemoryMappedViewAccessor view, uint offset, uint size)
		{
			Next = offset;
			m_view = view;
			m_end = offset + size;
		}

		public MappedFileCursor(MappedFileCursor cursor, uint offset, uint size)
		{
			Next = offset;
			m_view = cursor.m_view;
			m_end = offset + size;
		}

		public MappedFileCursor(MappedFileCursor cursor, uint size)
		{
			Next = cursor.Next;
			m_view = cursor.m_view;
			m_end = Next + size;
		}

		public MappedFileCursor(MappedFileCursor cursor)
		{
			Next = cursor.Next;
			m_view = cursor.m_view;
			m_end = cursor.m_end;
		}

		public MappedFileCursor(MemoryMappedViewAccessor view)
		{
			m_view = view;
		}

		#endregion


		#region Public Fields

		public uint Next;

		#endregion


		#region Properties

		public bool AtEnd { get { return Next >= m_end; } }
		public MemoryMappedViewAccessor View { get { return m_view; } }
		public uint End { get { return m_end; } }

		#endregion


		#region Private Fields

		uint m_end;
		MemoryMappedViewAccessor m_view;

		#endregion


		#region Public Methods

		public void Read<T>(out T res) where T : struct
		{
			m_view.Read(Next, out res);
			Next += (uint)Marshal.SizeOf<T>();
		}

		public byte ReadByte()
		{
			byte b = m_view.ReadByte(Next);
			Next += sizeof(byte);
			return b;
		}

		public ushort ReadUshort()
		{
			ushort us = m_view.ReadUInt16(Next);
			Next += sizeof(ushort);
			return us;
		}

		public uint ReadUint()
		{
			uint u = m_view.ReadUInt32(Next);
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
				ch = (char)m_view.ReadByte(Next++);
				if (ch == 0)
					break;
				stb.Append(ch);
			}
			return stb.ToString();
		}

		public string ReadString(uint off)
		{
			Next = off;
			return ReadString();
		}

		public void Write<T>(ref T res) where T : struct
		{
			m_view.Write(Next, ref res);
			Next += (uint)Marshal.SizeOf<T>();
		}

		public void WriteByte(byte b)
		{
			m_view.Write(Next, b);
			Next += sizeof(byte);
		}

		public void WriteUshort(ushort us)
		{
			m_view.Write(Next, us);
			Next += sizeof(ushort);
		}

		public void WriteUint(uint u)
		{
			m_view.Write(Next, u);
			Next += sizeof(uint);
		}

		public void WriteByteArray(byte[] arb)
		{
			m_view.WriteArray(Next, arb, 0, arb.Length);
			Next += (uint)arb.Length;
		}

		#endregion
	}
}
