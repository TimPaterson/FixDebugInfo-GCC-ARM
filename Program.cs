using System;
using System.Diagnostics;
using System.IO;

namespace FixDebugInfo;

class Program
{
	static void Main(string[] args)
	{
		long pos;
		ElfReadWrite elf;
		bool fDumpOnly = false;

		Debug.WriteLine(args[0]);
		if (args.Length > 1 && args[1] == "/d")
			fDumpOnly = true;

		Directory.SetCurrentDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
		elf = new ElfReadWrite(args[0]);
		elf.Open();
		if (fDumpOnly)
		{
			elf.DumpElfHeader();
			elf.DumpSectionHeaders();
			elf.DumpProgramHeaders();
			new DwarfReadWrite(elf).FixDebugInfo(0);
			return;
		}
		// Copy ELF
		elf.CopyElfHeader();
		elf.WriteProgramHeaders();
		pos = elf.CopyFixedSections();
		pos = new DwarfReadWrite(elf).FixDebugInfo(pos);
		pos = elf.CopySections(pos);
		elf.WriteSectionHeaders(pos);
		elf.Close();
	}
}
