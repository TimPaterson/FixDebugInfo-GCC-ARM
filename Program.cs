using System;
using System.Diagnostics;

namespace FixDebugInfo
{
	class Program
	{
		static void Main(string[] args)
		{
			ElfReader elf;
			DwarfReader dwarf;
			DwarfReader.LineResult lineRes;
			MappedFileCursor sectionCursor;
			MappedFileCursor unitCursor;
			ElfReader.SectionInfo section;
			uint pos;
			uint size;
			bool fDumpOnly = false;

			Debug.WriteLine(args[0]);
			if (args.Length > 1 && args[1] == "/d")
				fDumpOnly = true;
			elf = new ElfReader(args[0]);
			dwarf = new DwarfReader(elf);
			section = dwarf.OpenDebugLines();
			sectionCursor = section.Cursor;

			while (!sectionCursor.AtEnd)
			{
				size = dwarf.GetLineInfoSize(sectionCursor);
				if (size == 0)
					continue;	// skip unit, cursor already advanced

				unitCursor = new MappedFileCursor(sectionCursor, size);
				pos = unitCursor.Next;
				sectionCursor.Next += size;
				lineRes = dwarf.GetLineInfo(unitCursor);
				if (fDumpOnly)
				{
					DumpLines(lineRes);
					continue;
				}

				lineRes.lines = dwarf.FilterLines(lineRes.lines);
				//DumpLines(lineRes);
				unitCursor.Next = pos;
				dwarf.GenLineInfo(unitCursor, lineRes.lines);
			}
			elf.Close();
		}

		static void DumpLines(DwarfReader.LineResult lines)
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
			foreach (DwarfReader.LineItem line in lines.lines)
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
	}
}
