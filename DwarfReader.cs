using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FixDebugInfo
{
	class DwarfReader
	{
		const uint SkipUnit = 0xFFFFFFF0;

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

		#endregion


		#region Constructors

		public DwarfReader(ElfReader elf)
		{
			m_elf = elf;
		}

		#endregion


		#region Fields

		ElfReader m_elf;
		LineItem m_lineLast;
		LineHeaderPrefix m_linePrefix;
		LineHeader m_lineHead;
		LineHeaderV4 m_lineHeadV4;

		#endregion


		#region Public Methods

		public ElfReader.SectionInfo OpenDebugLines()
		{
			string path;

			m_elf.Open();
			path = Path.GetFullPath(m_elf.FileName);
			Directory.SetCurrentDirectory(Path.GetDirectoryName(path));
			return m_elf.FindSections(new string[] { ".debug_line" })[0];
		}

		public uint GetLineInfoSize(MappedFileCursor cur)
		{
			cur.Read(out m_linePrefix);
			if (m_linePrefix.unit_length >= SkipUnit)
			{
				// A special length value means we've processed this file
				// before and have a gap between units. Skip ahead
				// to the next unit and return 0 to inform the caller.
				cur.Next -= (uint)Marshal.SizeOf<LineHeaderPrefix>();
				uint u = m_linePrefix.unit_length - SkipUnit;
				if (u < (uint)Marshal.SizeOf<LineHeaderPrefix>())
					cur.Next += u;
				else
					cur.Next += m_linePrefix.header_length;
				return 0;
			}

			// Don't count version or header_length field we just read
			return m_linePrefix.unit_length - sizeof(ushort) - sizeof(uint);	
		}

		public LineResult GetLineInfo(MappedFileCursor cur)
		{
			LineResult line;

			line = RunLineProgram(cur);
			return line;
		}

		public void GenLineInfo(MappedFileCursor curOut, LineItem[] lines)
		{
			uint pos;

			// Currently positioned just after header_length field
			pos = curOut.Next - (uint)Marshal.SizeOf<LineHeaderPrefix>();
			curOut.Next += m_linePrefix.header_length;
			MakeLineProgram(curOut, lines);
			curOut.View.Write(pos, curOut.Next - pos - sizeof(uint));

			// Signal that space after this is unused. This is needed
			// if this program is run a second time on the same ELF file.
			pos = curOut.End - curOut.Next;
			if (pos == 0)
				return;
			if (pos < sizeof(uint))
				throw new Exception("Unable to patch tiny hole");
			if (pos < (uint)Marshal.SizeOf<LineHeaderPrefix>())
			{
				curOut.WriteUint(SkipUnit + pos);
				return;
			}

			m_linePrefix.unit_length = SkipUnit + (uint)Marshal.SizeOf<LineHeaderPrefix>();
			m_linePrefix.version = 0;
			m_linePrefix.header_length = pos;
			curOut.Write<LineHeaderPrefix>(ref m_linePrefix);
		}

		public LineItem[] FilterLines(LineItem[] lines)
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

		#endregion


		#region Private Methods

		int GetLeb(MappedFileCursor cur)
		{
			uint i;
			uint b;
			int shift;

			shift = 0;
			i = 0;
			do
			{
				b = cur.ReadByte();
				i += (b & 0x7F) << shift;
				shift += 7;
			} while ((b & 0x80) != 0);
			return (int)i;
		}

		int GetSignedLeb(MappedFileCursor cur)
		{
			uint i;
			uint b;
			int shift;

			shift = 0;
			i = 0;
			do
			{
				b = cur.ReadByte();
				i += (b & 0x7F) << shift;
				shift += 7;
			} while ((b & 0x80) != 0);
			if ((b & 0x40) != 0)
				return (int)i | -(1 << shift);
			return (int)i;
		}

		void WriteLeb(MappedFileCursor cur, int i)
		{
			byte b;
			do
			{
				b = (byte)(i & 0x7F);
				i >>= 7;
				if (i != 0)
					b |= 0x80;

				cur.WriteByte(b);
			} while (i != 0);
		}

		void WriteSignedLeb(MappedFileCursor cur, int i)
		{
			byte b;
			do
			{
				b = (byte)(i & 0x7F);
				i >>= 7;
				if ((i != 0 || (b & 0x40) != 0) && (i != -1 || (b & 0x40) == 0))
					b |= 0x80;
				cur.WriteByte(b);

			} while ((b & 0x80) != 0);
		}

		bool AddFile(MappedFileCursor cur, List<string> files, List<string> folders)
		{
			string str;
			int iDir;

			str = cur.ReadString();
			if (str.Length == 0)
				return false;

			iDir = GetLeb(cur); // directory index
			GetLeb(cur);    // file date
			GetLeb(cur);    // file size

			try
			{
				if (iDir != 0)
					str = Path.Combine(folders[iDir - 1], str);
				str = Path.GetFullPath(str);
			}
			catch (ArgumentException)
			{
			}
			files.Add(str);
			return true;
		}

		LineResult RunLineProgram(MappedFileCursor cur)
		{
			byte[] arbOpLen;
			List<string> arstrFolders;
			List<string> arstrFiles;
			List<LineItem> lstLine;
			string str;
			byte op;
			int iConstAddPc;
			int iLen;
			LineItem line;
			LineResult res;

			if (m_linePrefix.version == 4)
			{
				cur.Read(out m_lineHeadV4);
				if (m_lineHeadV4.maximum_operations_per_instruction != 1)
					Debug.WriteLine("Incompatible file format");
				m_lineHead.minimum_instruction_length = m_lineHeadV4.minimum_instruction_length;
				m_lineHead.default_is_stmt = m_lineHeadV4.default_is_stmt;
				m_lineHead.line_base = m_lineHeadV4.line_base;
				m_lineHead.line_range = m_lineHeadV4.line_range;
				m_lineHead.opcode_base = m_lineHeadV4.opcode_base;
			}
			else
				cur.Read(out m_lineHead);

			arbOpLen = new byte[m_lineHead.opcode_base];
			for (int i = 1; i < arbOpLen.Length; i++)
				arbOpLen[i] = cur.ReadByte();

			arstrFolders = new List<string>();
			for (; ; )
			{
				str = cur.ReadString();
				if (str.Length == 0)
					break;
				arstrFolders.Add(str);
			}

			arstrFiles = new List<string>();
			while (AddFile(cur, arstrFiles, arstrFolders)) ;

			iConstAddPc = (255 - m_lineHead.opcode_base) / m_lineHead.line_range * m_lineHead.minimum_instruction_length;
			lstLine = new List<LineItem>();

			while (!cur.AtEnd)
			{
				line.Address = 0;
				line.File = 1;
				line.Line = 1;
				line.Column = 0;
				line.Discr = 0;
				line.IsStmt = m_lineHead.default_is_stmt != 0;
				line.IsEnd = false;
				m_lineLast = line;

				while (!cur.AtEnd)
				{
					op = cur.ReadByte();
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
								iLen = GetLeb(cur) - 1;
								op = cur.ReadByte();
								switch ((ExtLineOp)op)
								{
									case ExtLineOp.DW_LNE_end_sequence:
										line.IsEnd = true;
										lstLine.Add(line);
										goto NextSequence;

									case ExtLineOp.DW_LNE_set_address:
										line.Address = (int)cur.ReadUint();
										break;

									case ExtLineOp.DW_LNE_define_file:
										AddFile(cur, arstrFiles, arstrFolders);
										Debug.WriteLine("File added inline");
										break;

									case ExtLineOp.DW_LNE_set_discriminator:
										line.Discr = GetLeb(cur);
										break;

									default:
										Debug.WriteLine($"Ignored extended opcode {op}");
										cur.Next += (uint)iLen;
										break;
								}
								break;

							case StdLineOp.DW_LNS_copy:
								lstLine.Add(line);
								m_lineLast = line;
								break;

							case StdLineOp.DW_LNS_advance_pc:
								line.Address += GetLeb(cur) * m_lineHead.minimum_instruction_length;
								break;

							case StdLineOp.DW_LNS_advance_line:
								line.Line += GetSignedLeb(cur);
								break;

							case StdLineOp.DW_LNS_set_file:
								line.File = GetLeb(cur);
								break;

							case StdLineOp.DW_LNS_set_column:
								line.Column = GetLeb(cur);
								break;

							case StdLineOp.DW_LNS_negate_stmt:
								line.IsStmt = !line.IsStmt;
								break;

							case StdLineOp.DW_LNS_const_add_pc:
								line.Address += iConstAddPc;
								break;

							case StdLineOp.DW_LNS_fixed_advance_pc:
								line.Address += cur.ReadUshort();
								break;

							case StdLineOp.DW_LNS_set_basic_block:
							case StdLineOp.DW_LNS_set_prologue_end:
							case StdLineOp.DW_LNS_set_epilogue_begin:
							default:
								Debug.WriteLine($"Ignored opcode {Enum.GetName(typeof(StdLineOp), op)}");
								for (int i = arbOpLen[op]; i > 0; i--)
									GetLeb(cur);
								break;
						}
					}
				}
		NextSequence: ;
			}

			res.lines = lstLine.ToArray();
			res.files = arstrFiles.ToArray();
			return res;
		}

		void MakeLineProgram(MappedFileCursor cur, LineItem[] lines)
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
						cur.WriteByte((byte)StdLineOp.DW_LNS_negate_stmt);

					if (line.Discr != lineLast.Discr)
					{
						cur.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
						// count of following bytes depends on size of LEB128
						byte cb = 2;
						uint val = (uint)line.Discr;
						while ((val >>= 7) != 0)
							cb++;
						cur.WriteByte(cb);
						cur.WriteByte((byte)ExtLineOp.DW_LNE_set_discriminator);
						WriteLeb(cur, line.Discr);
					}

					if (line.File != lineLast.File)
					{
						cur.WriteByte((byte)StdLineOp.DW_LNS_set_file);
						WriteLeb(cur, line.File);
					}
					if (line.Column != lineLast.Column)
					{
						cur.WriteByte((byte)StdLineOp.DW_LNS_set_column);
						WriteLeb(cur, line.Column);
					}

					iLineDif = line.Line - lineLast.Line;
					iAddrDif = (line.Address - lineLast.Address) / m_lineHead.minimum_instruction_length;

					if (iAddrDif < 0)
					{
						cur.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
						cur.WriteByte(5);
						cur.WriteByte((byte)ExtLineOp.DW_LNE_set_address);
						cur.WriteUint((uint)line.Discr);
						iAddrDif = 0;
					}

					if (line.IsEnd)
					{
						if (iLineDif != 0)
						{
							cur.WriteByte((byte)StdLineOp.DW_LNS_advance_line);
							WriteSignedLeb(cur, iLineDif);
						}
						if (iAddrDif != 0)
						{
							cur.WriteByte((byte)StdLineOp.DW_LNS_advance_pc);
							WriteLeb(cur, iAddrDif);
						}
						cur.WriteByte((byte)StdLineOp.DW_LNS_extended_op);
						cur.WriteByte(1);	// count of following bytes
						cur.WriteByte((byte)ExtLineOp.DW_LNE_end_sequence);
						break;
					}

					if (iLineDif < m_lineHead.line_base || iLineDif >= m_lineHead.line_base + m_lineHead.line_range)
					{
						// line dif exceeds range of special opcode
						cur.WriteByte((byte)StdLineOp.DW_LNS_advance_line);
						WriteSignedLeb(cur, iLineDif);
						iLineDif = 0;
					}

				FixOpcode:
					if (fFixIt || iAddrDif > iConstAddPc)
					{
						// address dif exceeds range of special opcode
						if (fFixIt || iAddrDif > iConstAddPc * 2)
						{
							// Big dif, use multi-byte opcode
							cur.WriteByte((byte)StdLineOp.DW_LNS_advance_pc);
							WriteLeb(cur, iAddrDif);
							iAddrDif = 0;
						}
						else
						{
							// Within range of single byte opcode
							cur.WriteByte((byte)StdLineOp.DW_LNS_const_add_pc);
							iAddrDif -= iConstAddPc;
						}
						fFixIt = false;
					}

					if (iAddrDif == 0 && iLineDif == 0)
						cur.WriteByte((byte)StdLineOp.DW_LNS_copy);
					else
					{
						// Compute special opcode
						iOpcode = iLineDif - m_lineHead.line_base + m_lineHead.line_range * iAddrDif + m_lineHead.opcode_base;
						if (iOpcode > 255)
						{
							fFixIt = true;
							goto FixOpcode;
						}
						cur.WriteByte((byte)iOpcode);
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
}
