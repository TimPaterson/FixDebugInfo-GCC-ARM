using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection.Metadata;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace FixDebugInfo;

class DwarfReadWrite(ElfReadWrite elf)
{
	#region Types

	public struct LineItem
	{
		public int Address;
		public int Line;
		public int File;
		public int Column;
		public int Discr;
		public bool IsStmt;
		public bool IsEnd;
	}

	public struct LineResult
	{
		public LineItem[] lines;
		public string[] files;
		public long headerStart;
		public uint headerSize;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct LineHeaderPrefix
	{
		public uint	unit_length;
		public ushort version;
		public uint	header_length;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct LineHeader
	{
		public byte minimum_instruction_length;
		public byte default_is_stmt;
		public sbyte line_base;
		public byte line_range;
		public byte opcode_base;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct LineHeaderV4
	{
		public byte minimum_instruction_length;
		public byte maximum_operations_per_instruction;
		public byte default_is_stmt;
		public sbyte line_base;
		public byte line_range;
		public byte opcode_base;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct CompUnitHeader
	{
		public uint unit_length;
		public ushort version;
		public uint debug_abbrev_offset;
		public byte address_size;
	}

	enum StdLineOp
	{
		DW_LNS_extended_op,
		DW_LNS_copy,
		DW_LNS_advance_pc,
		DW_LNS_advance_line,
		DW_LNS_set_file,
		DW_LNS_set_column,
		DW_LNS_negate_stmt,
		DW_LNS_set_basic_block,
		DW_LNS_const_add_pc,
		DW_LNS_fixed_advance_pc,
		DW_LNS_set_prologue_end,
		DW_LNS_set_epilogue_begin,
		DW_LNS_set_isa
	};

	enum ExtLineOp
	{
		DW_LNE_end_sequence = 1,
		DW_LNE_set_address,
		DW_LNE_define_file,
		DW_LNE_set_discriminator,
	};

	enum HasChildren
	{
		DW_CHILDREN_no,
		DW_CHILDREN_yes
	}

	enum Attribute
	{
		DW_AT_none = 0,
		DW_AT_stmt_list = 0x10,
	}

	enum Format
	{
		DW_FORM_none	= 0x00,
		DW_FORM_addr	= 0x01,
		DW_FORM_block2	= 0x03,
		DW_FORM_block4	= 0x04,
		DW_FORM_data2	= 0x05,
		DW_FORM_data4	= 0x06,
		DW_FORM_data8	= 0x07,
		DW_FORM_string	= 0x08,
		DW_FORM_block	= 0x09,
		DW_FORM_block1	= 0x0a,
		DW_FORM_data1	= 0x0b,
		DW_FORM_flag	= 0x0c,
		DW_FORM_sdata	= 0x0d,
		DW_FORM_strp	= 0x0e,
		DW_FORM_udata	= 0x0f,
		DW_FORM_ref_addr = 0x10,
		DW_FORM_ref1	= 0x11,
		DW_FORM_ref2	= 0x12,
		DW_FORM_ref4	= 0x13,
		DW_FORM_ref8	= 0x14,
		DW_FORM_ref_udata= 0x15,
		DW_FORM_indirect = 0x16,
		DW_FORM_sec_offset= 0x17,
		DW_FORM_exprloc	= 0x18,
		DW_FORM_flag_present= 0x19,
		DW_FORM_ref_sig8 = 0x20,
}

	enum SectionIndex
	{
		Line,
		Info,
		Abbrev,
	}

	#endregion


	#region Fields

	readonly ElfReadWrite m_elf = elf;
	readonly MemoryMappedViewAccessor m_inView = elf.InView;
	readonly MemoryMappedViewAccessor m_outView = elf.OutView;
	readonly ElfReadWrite.SectionInfo[] m_sections = elf.FindSections([".debug_line", ".debug_info", ".debug_abbrev"]);
	LineItem m_lineLast;
	LineHeaderPrefix m_linePrefix;
	LineHeader m_lineHead;
	LineHeaderV4 m_lineHeadV4;

	#endregion


	#region Public Methods

	public void FixLineInfo(MappedFileCursor lineCursor, MappedFileCursor? outCursor)
	{
		LineResult lineRes;

		lineRes = RunLineProgram(lineCursor);
		if (outCursor is null)
		{
			DumpLines(lineRes);
			return;
		}

		lineRes.lines = FilterLines(lineRes.lines);
		//DumpLines(lineRes);
		GenLineInfo(outCursor, lineRes);
	}

	public long FixDebugInfo(long offset)
	{
		int abbrev, code, tag;
		bool hasChildren;
		uint nextCompUnit;
		MappedFileCursor? outCursor = null;
		MappedFileCursor? patchCursor = null;
		MappedFileCursor infoCursor = m_sections[(int)(SectionIndex.Info)].GetCursor(m_inView);
		MappedFileCursor abbrCursor = m_sections[(int)(SectionIndex.Abbrev)].GetCursor(m_inView);
		MappedFileCursor lineCursor = m_sections[(int)(SectionIndex.Line)].GetCursor(m_inView);

		if (offset != 0)
		{
			patchCursor = new(m_outView, offset);   // patch new .debug_info section
			offset = m_elf.CopySection(offset, m_sections[(int)(SectionIndex.Info)].Index);
			m_elf.MarkSectionCopied(m_sections[(int)(SectionIndex.Line)].Index, offset);
			outCursor = new(m_outView, offset);     // new .debug_line section
		}

		while (!infoCursor.AtEnd)
		{
			infoCursor.Read(out CompUnitHeader head);
			nextCompUnit = infoCursor.Offset + head.unit_length - 7;
			abbrCursor.Offset = head.debug_abbrev_offset;
			abbrev = GetLeb(infoCursor);

			// search abbreviations for this code
			while (!abbrCursor.AtEnd)
			{
				code = GetLeb(abbrCursor);
				if (code == 0)
					break;	// UNDONE: didn't find what we needed
				tag = GetLeb(abbrCursor);
				hasChildren = (HasChildren)abbrCursor.ReadByte() == HasChildren.DW_CHILDREN_yes;

				while (!abbrCursor.AtEnd)
				{
					Attribute name = (Attribute)GetLeb(abbrCursor);
					Format form = (Format)GetLeb(abbrCursor);
					if (form == 0 && name == Attribute.DW_AT_none)
						break;
					if (code == abbrev)
					{
						if (name == Attribute.DW_AT_stmt_list)
						{
							uint linePointer = infoCursor.Offset;
							uint lineOffset = infoCursor.ReadUint();
							if (patchCursor is not null)
							{
								// patch debug_info pointer to line info
								patchCursor.Offset = linePointer;
								patchCursor.WriteUint(outCursor!.Offset);
							}
							lineCursor.Offset = lineOffset;
							FixLineInfo(lineCursor, outCursor);
							infoCursor.Offset = nextCompUnit;
							goto NextCompUnit;
						}

FormatSize:
						// advance position in .debug_info
						switch (form)
						{
							case Format.DW_FORM_data1:
							case Format.DW_FORM_ref1:
							case Format.DW_FORM_flag:
								infoCursor.Offset += 1;
								break;

							case Format.DW_FORM_data2:
							case Format.DW_FORM_ref2:
								infoCursor.Offset += 2;
								break;

							case Format.DW_FORM_addr:
							case Format.DW_FORM_data4:
							case Format.DW_FORM_ref4:
							case Format.DW_FORM_strp:
							case Format.DW_FORM_sec_offset:
							case Format.DW_FORM_ref_addr:
								infoCursor.Offset += 4;
								break;

							case Format.DW_FORM_data8:
							case Format.DW_FORM_ref8:
							case Format.DW_FORM_ref_sig8:
								infoCursor.Offset += 8;
								break;

							case Format.DW_FORM_block1:
								infoCursor.Offset += infoCursor.ReadByte();
								break;

							case Format.DW_FORM_block2:
								infoCursor.Offset += infoCursor.ReadUshort();
								break;

							case Format.DW_FORM_block4:
								infoCursor.Offset += infoCursor.ReadUint();
								break;

							case Format.DW_FORM_string:
								while (infoCursor.ReadByte() != 0) ;
								break;

							case Format.DW_FORM_block:
							case Format.DW_FORM_exprloc:
								infoCursor.Offset += (uint)GetLeb(infoCursor);
								break;

							case Format.DW_FORM_sdata:
							case Format.DW_FORM_udata:
							case Format.DW_FORM_ref_udata:
								GetLeb(infoCursor);
								break;

							case Format.DW_FORM_indirect:
								form = (Format)GetLeb(infoCursor);
								goto FormatSize;

							case Format.DW_FORM_flag_present:
								break;

							default:
								throw new Exception($"Unknown DWARF attribute form: {form:X}");
						}
					}
				}
			}
NextCompUnit:
			continue;
		}
		if (outCursor is not null)
		{
			m_elf.SetSectionSize(m_sections[(int)(SectionIndex.Line)].Index, outCursor.Offset);
			return outCursor.Next;
		}

		return offset;
	}

	#endregion


	#region Private Methods

	static void DumpLines(LineResult lines)
	{
		int i;

		Console.WriteLine("Files:");
		i = 1;
		foreach (string file in lines.files)
		{
			Console.WriteLine($"{i} {file}");
			i++;
		}

		Console.WriteLine("Line List:");
		foreach (DwarfReadWrite.LineItem line in lines.lines)
		{
			Console.Write($"addr: {line.Address:X}");
			Console.Write($", line: {line.Line}");
			Console.Write($", file: {line.File}");
			Console.Write($", stmt: {line.IsStmt}");
			Console.Write($", col: {line.Column}");
			Console.Write($", discr: {line.Discr}");
			if (line.IsEnd)
				Console.Write(", end");
			Console.WriteLine();
		}
		Console.WriteLine();
	}

	void GenLineInfo(MappedFileCursor outCursor, LineResult lineRes)
	{
		long pos;

		pos = outCursor.Next;     // remember location of unit_length
		outCursor.Write(ref m_linePrefix);
		outCursor.Write(ref m_lineHead);

		// copy folder names, file names, and opcode lengths
		m_elf.CopyFile(lineRes.headerStart, outCursor.Next, (int)lineRes.headerSize);
		outCursor.Offset += lineRes.headerSize;

		MakeLineProgram(outCursor, lineRes.lines);
		outCursor.View.Write(pos, (uint)(outCursor.Next - pos - sizeof(uint)));   // patch unit_length
	}

	static LineItem[] FilterLines(LineItem[] lines)
	{
		int posIn, posOut;
		LineItem[] linesOut;

		if (lines.Length == 0)
			return lines;

		posOut = 0;

		for (posIn = 1;  posIn < lines.Length; )
		{
			if (lines[posIn].IsEnd)
			{
				lines[++posOut] = lines[posIn++];   // copy end
				if (posIn < lines.Length)
					lines[++posOut] = lines[posIn++];   // copy next
				continue;
			}

			// All lines we keep except end sequence will be marked
			// as start of statement
			lines[posIn].IsStmt = true;

			// Remove previous lines with same address
			if (lines[posIn].Address == lines[posOut].Address)
				lines[posOut] = lines[posIn++];

			else if (posOut > 0 && lines[posOut].Line == lines[posOut - 1].Line && lines[posOut].File == lines[posOut - 1].File)
			{
				posOut--;

				// Remove all subsequent lines if same line number (& file)
				while (posIn < lines.Length &&
					lines[posOut].Line == lines[posIn].Line &&
					lines[posOut].File == lines[posIn].File &&
					!lines[posIn].IsEnd)
				{
					posIn++;
				}
			}
			else
				lines[++posOut] = lines[posIn++];   // copy line
		}

		if (lines.Length == ++posOut)
			return lines;
		linesOut = new LineItem[posOut];
		Array.Copy(lines, linesOut, posOut);
		return linesOut;
	}

	static int GetLeb(MappedFileCursor cursor)
	{
		uint i;
		uint b;
		int shift;

		shift = 0;
		i = 0;
		do
		{
			b = cursor.ReadByte();
			i += (b & 0x7F) << shift;
			shift += 7;
		} while ((b & 0x80) != 0);
		return (int)i;
	}

	static int GetSignedLeb(MappedFileCursor cursor)
	{
		uint i;
		uint b;
		int shift;

		shift = 0;
		i = 0;
		do
		{
			b = cursor.ReadByte();
			i += (b & 0x7F) << shift;
			shift += 7;
		} while ((b & 0x80) != 0);
		if ((b & 0x40) != 0)
			return (int)i | -(1 << shift);
		return (int)i;
	}

	static void WriteLeb(MappedFileCursor outCursor, int i)
	{
		byte b;
		do
		{
			b = (byte)(i & 0x7F);
			i >>= 7;
			if (i != 0)
				b |= 0x80;

			outCursor.WriteByte(b);
		} while (i != 0);
	}

	static void WriteSignedLeb(MappedFileCursor outCursor, int i)
	{
		byte b;
		do
		{
			b = (byte)(i & 0x7F);
			i >>= 7;
			if ((i != 0 || (b & 0x40) != 0) && (i != -1 || (b & 0x40) == 0))
				b |= 0x80;
			outCursor.WriteByte(b);

		} while ((b & 0x80) != 0);
	}

	static bool AddFile(MappedFileCursor cursor, List<string> files, List<string> folders)
	{
		string str;
		int iDir;

		str = cursor.ReadString();
		if (str.Length == 0)
			return false;

		iDir = GetLeb(cursor); // directory index
		GetLeb(cursor);    // file date
		GetLeb(cursor);    // file size

		try
		{
			if (iDir != 0)
				str = Path.Combine(folders[iDir - 1], str);
			str = Path.GetFullPath(str);
		}
		catch (ArgumentException)
		{
			Debug.WriteLine($"Ignoring invalid path {str}");
		}
		files.Add(str);
		return true;
	}

	LineResult RunLineProgram(MappedFileCursor lineCursor)
	{
		byte[] arbOpLen;
		List<string> arstrFolders;
		List<string> arstrFiles;
		List<LineItem> lstLine;
		string str;
		byte op;
		int constAddPc;
		int len;
		LineItem line;
		LineResult res;

		lineCursor.Read(out m_linePrefix);
		lineCursor.End = lineCursor.Next + m_linePrefix.unit_length - sizeof(uint) - sizeof(ushort);

		if (m_linePrefix.version == 4)
		{
			lineCursor.Read(out m_lineHeadV4);
			if (m_lineHeadV4.maximum_operations_per_instruction != 1)
				throw new Exception("Incompatible file format");

			m_lineHead.minimum_instruction_length = m_lineHeadV4.minimum_instruction_length;
			m_lineHead.default_is_stmt = m_lineHeadV4.default_is_stmt;
			m_lineHead.line_base = m_lineHeadV4.line_base;
			m_lineHead.line_range = m_lineHeadV4.line_range;
			m_lineHead.opcode_base = m_lineHeadV4.opcode_base;
			uint adj = (uint)(Marshal.SizeOf<LineHeaderV4>() - Marshal.SizeOf<LineHeader>());
			m_linePrefix.unit_length -= adj;
			m_linePrefix.header_length -= adj;
			m_linePrefix.version = 3;
		}
		else if (m_linePrefix.version == 3 || m_linePrefix.version == 2)
			lineCursor.Read(out m_lineHead);
		else
			throw new Exception($"Unsupported DWARF version {m_linePrefix.version}");

		if (m_lineHead.minimum_instruction_length == 0)
			throw new Exception("Invalid file");

		res.headerStart = lineCursor.Next;		// so we can copy it to the output file

		arbOpLen = new byte[m_lineHead.opcode_base];
		for (int i = 1; i < arbOpLen.Length; i++)
			arbOpLen[i] = lineCursor.ReadByte();

		arstrFolders = [];
		for (; ; )
		{
			str = lineCursor.ReadString();
			if (str.Length == 0)
				break;
			arstrFolders.Add(str);
		}

		arstrFiles = [];
		while (AddFile(lineCursor, arstrFiles, arstrFolders)) ;

		res.headerSize = (uint)(lineCursor.Next - res.headerStart);

		constAddPc = (255 - m_lineHead.opcode_base) / m_lineHead.line_range * m_lineHead.minimum_instruction_length;
		lstLine = [];

		while (!lineCursor.AtEnd)
		{
			line.Address = 0;
			line.File = 1;
			line.Line = 1;
			line.Column = 0;
			line.Discr = 0;
			line.IsStmt = m_lineHead.default_is_stmt != 0;
			line.IsEnd = false;
			m_lineLast = line;

			while (!lineCursor.AtEnd)
			{
				op = lineCursor.ReadByte();
				if (op >= m_lineHead.opcode_base)
				{
					// Special opcode
					op -= m_lineHead.opcode_base;
					line.Address += op / m_lineHead.line_range * m_lineHead.minimum_instruction_length;
					line.Line += op % m_lineHead.line_range + m_lineHead.line_base;
					lstLine.Add(line);
					m_lineLast = line;
				}
				else
				{
					switch ((StdLineOp)op)
					{
						case StdLineOp.DW_LNS_extended_op:
							len = GetLeb(lineCursor) - 1;
							op = lineCursor.ReadByte();
							switch ((ExtLineOp)op)
							{
								case ExtLineOp.DW_LNE_end_sequence:
									line.IsEnd = true;
									lstLine.Add(line);
									goto NextSequence;

								case ExtLineOp.DW_LNE_set_address:
									line.Address = (int)lineCursor.ReadUint();
									break;

								case ExtLineOp.DW_LNE_define_file:
									AddFile(lineCursor, arstrFiles, arstrFolders);
									Debug.WriteLine("File added inline");
									break;

								case ExtLineOp.DW_LNE_set_discriminator:
									line.Discr = GetLeb(lineCursor);
									break;

								default:
									Debug.WriteLine($"Ignored extended opcode {op}");
									lineCursor.Next += (uint)len;
									break;
							}
							break;

						case StdLineOp.DW_LNS_copy:
							lstLine.Add(line);
							m_lineLast = line;
							break;

						case StdLineOp.DW_LNS_advance_pc:
							line.Address += GetLeb(lineCursor) * m_lineHead.minimum_instruction_length;
							break;

						case StdLineOp.DW_LNS_advance_line:
							line.Line += GetSignedLeb(lineCursor);
							break;

						case StdLineOp.DW_LNS_set_file:
							line.File = GetLeb(lineCursor);
							break;

						case StdLineOp.DW_LNS_set_column:
							line.Column = GetLeb(lineCursor);
							break;

						case StdLineOp.DW_LNS_negate_stmt:
							line.IsStmt = !line.IsStmt;
							break;

						case StdLineOp.DW_LNS_const_add_pc:
							line.Address += constAddPc;
							break;

						case StdLineOp.DW_LNS_fixed_advance_pc:
							line.Address += lineCursor.ReadUshort();
							break;

						case StdLineOp.DW_LNS_set_basic_block:
						case StdLineOp.DW_LNS_set_prologue_end:
						case StdLineOp.DW_LNS_set_epilogue_begin:
						default:
							Debug.WriteLine($"Ignored opcode {Enum.GetName(typeof(StdLineOp), op)}");
							for (int i = arbOpLen[op]; i > 0; i--)
								GetLeb(lineCursor);
							break;
					}
				}
			}
	NextSequence: 
			continue;
		}

		res.lines = [.. lstLine];
		res.files = [.. arstrFiles];
		return res;
	}

	void MakeLineProgram(MappedFileCursor outCursor, LineItem[] lines)
	{
		LineItem line;
		LineItem lineLast;
		int iLine;
		int iOpcode;
		int iConstAddPc;
		int iAddrDif;
		int iLineDif;
		bool fFixIt;

		iConstAddPc = (255 - m_lineHead.opcode_base) / m_lineHead.line_range;
		iLine = 0;
		fFixIt = false;

		for (; ; )
		{
			if (iLine >= lines.Length)
				break;

			lineLast.Address = 0;
			lineLast.File = 1;
			lineLast.Line = 1;
			lineLast.Column = 0;
			lineLast.Discr = 0;
			lineLast.IsStmt = m_lineHead.default_is_stmt != 0;

			for (; ; )
			{
				line = lines[iLine++];

				if (line.IsStmt != lineLast.IsStmt)
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_negate_stmt);

				if (line.Discr != lineLast.Discr)
				{
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
					// count of following bytes depends on size of LEB128
					byte cb = 2;
					uint val = (uint)line.Discr;
					while ((val >>= 7) != 0)
						cb++;
					outCursor.WriteByte(cb);
					outCursor.WriteByte((byte)ExtLineOp.DW_LNE_set_discriminator);
					WriteLeb(outCursor, line.Discr);
				}

				if (line.File != lineLast.File)
				{
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_set_file);
					WriteLeb(outCursor, line.File);
				}
				if (line.Column != lineLast.Column)
				{
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_set_column);
					WriteLeb(outCursor, line.Column);
				}

				iLineDif = line.Line - lineLast.Line;
				iAddrDif = (line.Address - lineLast.Address) / m_lineHead.minimum_instruction_length;

				if (iAddrDif < 0)
				{
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
					outCursor.WriteByte(5);
					outCursor.WriteByte((byte)ExtLineOp.DW_LNE_set_address);
					outCursor.WriteUint((uint)line.Discr);
					iAddrDif = 0;
				}

				if (line.IsEnd)
				{
					if (iLineDif != 0)
					{
						outCursor.WriteByte((byte)StdLineOp.DW_LNS_advance_line);
						WriteSignedLeb(outCursor, iLineDif);
					}
					if (iAddrDif != 0)
					{
						outCursor.WriteByte((byte)StdLineOp.DW_LNS_advance_pc);
						WriteLeb(outCursor, iAddrDif);
					}
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
					outCursor.WriteByte(1);	// count of following bytes
					outCursor.WriteByte((byte)ExtLineOp.DW_LNE_end_sequence);
					break;
				}

				if (iLineDif < m_lineHead.line_base || iLineDif >= m_lineHead.line_base + m_lineHead.line_range)
				{
					// line dif exceeds range of special opcode
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_advance_line);
					WriteSignedLeb(outCursor, iLineDif);
					iLineDif = 0;
				}

			FixOpcode:
				if (fFixIt || iAddrDif > iConstAddPc)
				{
					// address dif exceeds range of special opcode
					if (fFixIt || iAddrDif > iConstAddPc * 2)
					{
						// Big dif, use multi-byte opcode
						outCursor.WriteByte((byte)StdLineOp.DW_LNS_advance_pc);
						WriteLeb(outCursor, iAddrDif);
						iAddrDif = 0;
					}
					else
					{
						// Within range of single byte opcode
						outCursor.WriteByte((byte)StdLineOp.DW_LNS_const_add_pc);
						iAddrDif -= iConstAddPc;
					}
					fFixIt = false;
				}

				if (iAddrDif == 0 && iLineDif == 0)
					outCursor.WriteByte((byte)StdLineOp.DW_LNS_copy);
				else
				{
					// Compute special opcode
					iOpcode = iLineDif - m_lineHead.line_base + m_lineHead.line_range * iAddrDif + m_lineHead.opcode_base;
					if (iOpcode > 255)
					{
						fFixIt = true;
						goto FixOpcode;
					}
					outCursor.WriteByte((byte)iOpcode);
				}

				// Move on to next line
				if (iLine >= lines.Length)
					throw new Exception("Line list did not end with End Sequence");
				lineLast = line;
			}
		}
	}

	#endregion
}
