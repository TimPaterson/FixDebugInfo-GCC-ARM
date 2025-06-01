using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace FixDebugInfo;

class ElfReadWrite(string inFileName) : IDisposable
{
	const int FileBufferSize = 65536;

	#region Types

	enum SecType : uint
	{
		SHT_NOBITS = 8,
	}

	public struct SectionInfo
	{
		public int Index;
		public uint	Position;
		public uint Size;

		public MappedFileCursor GetCursor(MemoryMappedViewAccessor view) => new(view, Position, Size);
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
		public ushort e_shstrndx;	// section header index of string table
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct SectionHeader
	{
		public uint sh_name;		// index of name in string table
		public SecType sh_type;
		public uint sh_flags;
		public uint sh_addr;
		public uint sh_offset;		// offset in file of section
		public uint sh_size;		// size of section
		public uint sh_link;
		public uint sh_info;
		public uint sh_addralign;
		public uint sh_entsize;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct ProgramHeader
	{
		public uint p_type;
		public uint p_offset;
		public uint p_vaddr;
		public uint p_paddr;
		public uint p_filesz;
		public uint p_memsz;
		public uint p_flags;
		public uint p_align;
	}

	#endregion


	#region Fields

	readonly string m_inFileName = inFileName;
	readonly byte[] m_fileBuffer = new byte[FileBufferSize];

	uint m_offsetStrTable;
	long m_fileEnd;
	string? m_outFileName;
	FileStream? m_outStream;
	ElfHeader m_elfHeader;
	MemoryMappedFile? m_mapOutFile;
	MemoryMappedFile? m_mapInFile;
	MemoryMappedViewAccessor? m_inView;
	MemoryMappedViewAccessor? m_outView;
	SectionHeader[] m_sectionHeaders = null!;
	bool[] m_isSectionCopied = null!;
	ProgramHeader[] m_programHeaders = null!;

	#endregion


	#region Properties

	public string FileName => m_inFileName;
	public MemoryMappedViewAccessor OutView => m_outView!;
	public MemoryMappedViewAccessor InView => m_inView!;

	#endregion


	#region Public Methods

	public void Open()
	{
		m_mapInFile = MemoryMappedFile.CreateFromFile(m_inFileName);
		m_inView = m_mapInFile.CreateViewAccessor();
		m_inView.Read(0, out m_elfHeader);
		m_programHeaders = new ProgramHeader[m_elfHeader.e_phnum];
		m_sectionHeaders = new SectionHeader[m_elfHeader.e_shnum];
		m_isSectionCopied = new bool[m_elfHeader.e_shnum];
		m_inView.ReadArray(m_elfHeader.e_phoff, m_programHeaders, 0, m_elfHeader.e_phnum);
		m_inView.ReadArray(m_elfHeader.e_shoff, m_sectionHeaders, 0, m_elfHeader.e_shnum);

		// Get string table section
		m_offsetStrTable = m_sectionHeaders[m_elfHeader.e_shstrndx].sh_offset;
	}

	public void Close()
	{
		if (m_inView is not null)
		{
			m_inView.Dispose();
			m_inView = null;
		}
		if (m_outView is not null)
		{
			m_outView.Dispose();
			m_outView = null;
		}
		if (m_mapInFile is not null)
		{
			m_mapInFile.Dispose();
			m_mapInFile = null;
		}
		if (m_mapOutFile is not null)
		{
			m_mapOutFile.Dispose();
			m_mapOutFile = null;
		}
		if (m_outStream is not null)
		{
			m_outStream.SetLength(m_fileEnd);
			m_outStream.Close();
			m_outStream = null;
			File.Move(m_outFileName!, FileName, true);
		}
	}

	public void CopyElfHeader() { CopyFile(0, 0, m_elfHeader.e_ehsize); }

	public void WriteProgramHeaders() 
	{ 
		m_outView!.WriteArray(m_elfHeader.e_phoff, m_programHeaders, 0, m_elfHeader.e_phnum); 
	}

	public void WriteSectionHeaders(long offset) 
	{
		offset = (offset + 3) & ~3;		// ensure 4-byte aligned
		m_elfHeader.e_shoff = (uint)offset;
		m_outView!.WriteArray(offset, m_sectionHeaders, 0, m_elfHeader.e_shnum);
		m_fileEnd = offset + m_elfHeader.e_shnum * m_elfHeader.e_shentsize;
		m_outView.Write(0, ref m_elfHeader);	// rewrite updated ELF header	
	}

	public long CopySection(long offset, int index)
	{
		long end;

		if (m_isSectionCopied[index] || m_sectionHeaders[index].sh_type == SecType.SHT_NOBITS)
			return offset;

		end = CopyFile(m_sectionHeaders[index].sh_offset, offset, (int)m_sectionHeaders[index].sh_size);
		m_sectionHeaders[index].sh_offset = (uint)offset;
		m_isSectionCopied[index] = true;    // mark it as copied
		uint align = m_sectionHeaders[index].sh_addralign;
		if (align > 1)
			end = (end + align - 1) & ~(align - 1);
		return end;
	}

	public long CopySections(long offset)
	{
		for (int i = 0; i < m_elfHeader.e_shnum; i++)
			offset = CopySection(offset, i);
		return offset;
	}

	public long CopyFixedSections()
	{
		long maxOffset = 0;
		long end;
		uint endFixed = GetProgEnd();

		for (int i = 0; i < m_elfHeader.e_shnum; i++)
		{
			if (m_sectionHeaders[i].sh_offset < endFixed)
			{
				end = CopySection(m_sectionHeaders[i].sh_offset, i);
				if (end > maxOffset)
					maxOffset = end;
			}
		}
		return maxOffset;
	}

	public long CopyFile(long srcOffset, long dstOffset, int length)
	{
		m_outFileName ??= m_inFileName + ".tmp";
		m_outStream ??= new FileStream(m_outFileName, FileMode.Create);
		m_mapOutFile ??= MemoryMappedFile.CreateFromFile(m_outStream, null, m_inView!.Capacity * 2, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
		m_outView ??= m_mapOutFile.CreateViewAccessor();

		while (length > 0)
		{
			int cnt = length;
			if (cnt > FileBufferSize)
				cnt = FileBufferSize;
			cnt = m_inView!.ReadArray(srcOffset, m_fileBuffer, 0, cnt);
			m_outView.WriteArray(dstOffset, m_fileBuffer, 0, cnt);
			length -= cnt;
			srcOffset += cnt;
			dstOffset += cnt;
		}
		return dstOffset;
	}

	public void DumpElfHeader()
	{
		Console.WriteLine($"Header size: {m_elfHeader.e_ehsize}, expected {Marshal.SizeOf<ElfHeader>()}");
		Console.WriteLine($"Program header start: {m_elfHeader.e_phoff,7}, len: {m_elfHeader.e_phentsize * m_elfHeader.e_phnum}, expected: {Marshal.SizeOf<ProgramHeader>() * m_elfHeader.e_phnum}");
		Console.WriteLine($"Section header start: {m_elfHeader.e_shoff,7}, len: {m_elfHeader.e_shentsize * m_elfHeader.e_shnum}, expected: {Marshal.SizeOf<SectionHeader>() * m_elfHeader.e_shnum}");
	}

	public void DumpSectionHeaders()
	{
		string strSecName;
		MappedFileCursor stringCursor;

		stringCursor = new(m_inView!, m_offsetStrTable);
		for (int i = 1; i < m_elfHeader.e_shnum; i++)
		{
			strSecName = stringCursor.ReadString(m_sectionHeaders[i].sh_name);
			Console.WriteLine($"{strSecName,-15} start: {m_sectionHeaders[i].sh_offset,7} len: {m_sectionHeaders[i].sh_size,6}");
		}
	}

	public void DumpProgramHeaders()
	{
		uint off;

		off = m_elfHeader.e_phoff;
		for (int i = 0; i < m_elfHeader.e_phnum; i++, off += m_elfHeader.e_phentsize)
		{
			m_inView!.Read(off, out ProgramHeader head);
			Console.WriteLine($"start: {head.p_offset,7} len: {head.p_filesz,6}");
		}
	}

	public SectionInfo[] FindSections(string[] secNames)
	{
		int cntFound;
		string secName;
		SectionInfo[] secInfos;
		MappedFileCursor stringCursor;

		cntFound = 0;
		secInfos = new SectionInfo[secNames.Length];
		stringCursor = new(m_inView!, m_offsetStrTable);
		for (int i = 1; i < m_elfHeader.e_shnum; i++)
		{
			secName = stringCursor.ReadString(m_sectionHeaders[i].sh_name);
			for (int j = 0; j < secNames.Length; j++)
			{
				if (secName == secNames[j])
				{
					secInfos[j].Index = i;
					secInfos[j].Position = m_sectionHeaders[i].sh_offset;
					secInfos[j].Size = m_sectionHeaders[i].sh_size;
					cntFound++;
					if (cntFound == secNames.Length)
						return secInfos;
					break;
				}
			}
		}
		return secInfos;
	}

	public void SetSectionSize(int index, uint size)
	{
		m_sectionHeaders[index].sh_size = size;
	}

	public void MarkSectionCopied(int index, long offset)
	{ 
		m_isSectionCopied[index] = true; 
		m_sectionHeaders[index].sh_offset = (uint)offset;
	}

	#endregion


	#region Private Methods

	uint GetProgEnd()
	{
		uint maxOffset = 0;

		for (int i = 0; i < m_programHeaders.Length; i++)
		{
			uint end = m_programHeaders[i].p_offset + m_programHeaders[i].p_filesz;
			if (end > maxOffset)
				maxOffset = end;
		}
		return maxOffset;
	}

	#endregion


	#region IDisposable Members

	public void Dispose()
	{
		Close();
	}

	#endregion
}
