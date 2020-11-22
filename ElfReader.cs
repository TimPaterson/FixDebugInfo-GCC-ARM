using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace FixDebugInfo
{
	class ElfReader : IDisposable
	{
		#region Types

		public struct SectionInfo
		{
			public uint Offset;
			public MappedFileCursor Cursor;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		unsafe struct ElfHeader
		{
			public fixed byte e_ident[16];
			public ushort e_type;
			public ushort e_machine;
			public uint e_version;
			public uint e_entry;
			public uint e_phoff;		// program header offset
			public uint e_shoff;		// section header offset
			public uint e_flags;
			public ushort e_ehsize;		// ELF header size
			public ushort e_phentsize;	// program header entry size
			public ushort e_phnum;		// count of program header entries
			public ushort e_shentsize;	// section header entry size
			public ushort e_shnum;		// count of section header entries
			public ushort e_shstrndx;	// section header index of name table
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		struct SectionEntry
		{
			public uint sh_name;		// index of name in string table
			public uint sh_type;
			public uint sh_flags;
			public uint sh_addr;
			public uint sh_offset;		// offset in file of section
			public uint sh_size;		// size of section
			public uint sh_link;
			public uint sh_info;
			public uint sh_addralign;
			public uint sh_entsize;
		}

		#endregion


		#region Constructors

		public ElfReader(string strFileName)
		{
			m_strFileName = strFileName;
		}

		#endregion


		#region Fields

		string m_strFileName;
		MemoryMappedFile m_map;
		MemoryMappedViewAccessor m_view;
		uint m_oSecHeader;
		uint m_oSecEntrySize;
		uint m_cSecEntry;
		uint m_oNameTable;

		#endregion


		#region Properties

		public string FileName { get { return m_strFileName; } }

		#endregion


		#region Public Methods

		public void Open()
		{
			ElfHeader ehead;
			SectionEntry sec;
			uint off;

			m_map = MemoryMappedFile.CreateFromFile(m_strFileName);
			m_view = m_map.CreateViewAccessor();
			m_view.Read<ElfHeader>(0, out ehead);
			m_oSecHeader = ehead.e_shoff;
			m_oSecEntrySize = ehead.e_shentsize;
			m_cSecEntry = ehead.e_shnum;
			off = m_oSecEntrySize * ehead.e_shstrndx + m_oSecHeader;
			m_view.Read<SectionEntry>(off, out sec);
			m_oNameTable = sec.sh_offset;
		}

		public void Close()
		{
			if (m_view != null)
			{
				m_view.Dispose();
				m_view = null;
			}
			if (m_map != null)
			{
				m_map.Dispose();
				m_map = null;
			}
		}

		public SectionInfo[] FindSections(string[] arstrSecName)
		{
			int cFound;
			uint off;
			string strSecName;
			SectionEntry sec;
			SectionInfo[] arSections;
			MappedFileCursor curString;

			cFound = 0;
			arSections = new SectionInfo[arstrSecName.Length];
			curString = new MappedFileCursor(m_view);
			off = m_oSecHeader + m_oSecEntrySize;	// skip first entry
			for (int i = 1; i < m_cSecEntry; i++, off += m_oSecEntrySize)
			{
				m_view.Read<SectionEntry>(off, out sec);
				strSecName = curString.ReadString(m_oNameTable + sec.sh_name);
				for (int j = 0; j < arstrSecName.Length; j++)
				{
					if (strSecName == arstrSecName[j])
					{
						if (arSections[j].Cursor == null)
						{
							arSections[j].Offset = off;
							arSections[j].Cursor = new MappedFileCursor(m_view, sec.sh_offset, sec.sh_size);
							cFound++;
							if (cFound == arstrSecName.Length)
								return arSections;
						}
						break;
					}
				}
			}
			return arSections;
		}

		public void SetSectionSize(uint offset, uint size)
		{
			offset += (uint)Marshal.OffsetOf<SectionEntry>("sh_size");
			m_view.Write(offset, size);
		}

		#endregion


		#region Private Methods

		#endregion


		#region IDisposable Members

		public void Dispose()
		{
			Close();
		}

		#endregion
	}
}
