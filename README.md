# Restore Microchip Studio debugging after upgrading to ARM GCC 9.3
When using Microchip Studio to develop for an ARM MCU there are significant 
advantages to upgrading the ancient GCC 6 compiler shipped with AS7.
The [GCC 9 compiler](https://developer.arm.com/tools-and-software/open-source-software/developer-tools/gnu-toolchain/gnu-rm)
provides support through C++17 and appears to generate noticeably smaller
code. However, the debugging information -- specifically the mapping
from address to source line -- confuses the Microchip Studio debugger. This
results in an inability to set breakpoints on a source line and step
through code at the source level.

In comparing a dump of the line mapping info, the problem seems to 
be not a lack of information, but too much. The new compiler generates
multiple entries at the same address and multiple entries for the
same line. This baffles the disassembler in the debugger (but
not the disassembler in the objdump utility).

FixDebugInfo corrects this by passing over the executable `.elf` file
and rewriting the line mapping info for each compilation unit. This 
can be added as a post-build step in Microchip Studio
(assuming FixDebugInfo.exe is in the search path):

`FixDebugInfo $(OutputDirectory)\$(OutputFileName)$(OutputFileExtension)`

FixDebugInfo can also simply dump the line number information to
`stdout` without modifying the file by adding `/d` after the file name
(there's a lot of output, so redirect it to a file):

`FixDebugInfo file.elf /d >file.txt`
### Program Notes
This program is written in C# and requires .NET 9.0. 

The orignal version of this program modified the `.elf` file in place, and 
was unsuccessful if that wouldn't work. The program now fully rewrites the
`.elf` file and no longer fails.

Compilers since GCC 9.3.1 seem to have further changed details of their
debugging information. Even after running FixDebugInfo, Microchip Studio
doesn't like it.
### Example
Here is snippet of code that demonstrates the problem and solution.
First, here is the listing file produced by objdump of GCC 9.3.1 code:

            if (ch >= 'A' && ch <= 'Z')
    134e:   0003        movs    r3, r0
    1350:   3b41        subs    r3, #65 ; 0x41
    1352:   b2db        uxtb    r3, r3
    1354:   2b19        cmp r3, #25
    1356:   d801        bhi.n   135c <main+0x1f8>
                ch += 'a' - 'A';
    1358:   3020        adds    r0, #32
    135a:   b2c0        uxtb    r0, r0

            switch (ch)
    135c:   2877        cmp r0, #119    ; 0x77
    135e:   d106        bne.n   136e <main+0x20a>
            {
                // Use lower-case alphabetical order to find the right letter
            case 'w':
                // Trigger WDT
                DEBUG_PRINT("Triggering WDT\n");
    1360:   4864        ldr r0, [pc, #400]  ; (14f4 <main+0x390>)
    1362:   f000 fbd3   bl  1b0c <puts>
                for (;;);
    1366:   e7fe        b.n 1366 <main+0x202>
        iTmp &= ~ID_Right;
    1368:   2205        movs    r2, #5
    136a:   4393        bics    r3, r2
    136c:   e7ab        b.n 12c6 <main+0x162>
However, the disassembly in the Microchip Studio debugger looks like this:

                if (ch >= 'A' && ch <= 'Z')
    0000134E 03.00                 movs r3, r0       
    00001350 41.3b                 subs r3, #65      
    --- No source file -------------------------------------------------------------
    00001352 db.b2                 uxtb r3, r3       
    00001354 19.2b                 cmp  r3, #25      
    00001356 01.d8                 bhi  #2       
    00001358 20.30                 adds r0, #32      
    0000135A c0.b2                 uxtb r0, r0       
    0000135C 77.28                 cmp  r0, #119         
    0000135E 06.d1                 bne  #12      
    00001360 64.48                 ldr  r0, [pc, #400]       
    00001362 00.f0.d3.fb           bl   #1958        
    00001366 fe.e7                 b    #-4      
    00001368 05.22                 movs r2, #5       
    0000136A 93.43                 bics r3, r2       
    0000136C ab.e7                 b    #-170        
The line number info from the GCC 9.3.1 compiler:

    addr: 134E, line: 365, file: 5, stmt: True
    addr: 134E, line: 365, file: 5, stmt: False
    addr: 1352, line: 365, file: 5, stmt: False
    addr: 1358, line: 366, file: 5, stmt: True
    addr: 1358, line: 366, file: 5, stmt: False
    addr: 135A, line: 366, file: 5, stmt: False
    addr: 135C, line: 368, file: 5, stmt: True
    addr: 1360, line: 371, file: 5, stmt: True
    addr: 1360, line: 373, file: 5, stmt: True
    addr: 1360, line: 373, file: 5, stmt: False
    addr: 1362, line: 373, file: 5, stmt: False
    addr: 1366, line: 374, file: 5, stmt: True
    addr: 1366, line: 374, file: 5, stmt: True
    addr: 1368, line: 374, file: 5, stmt: False
    addr: 1368, line: 320, file: 5, stmt: True
    addr: 1368, line: 320, file: 5, stmt: False
    addr: 136C, line: 320, file: 5, stmt: False
    addr: 136E, line: 381, file: 5, stmt: True
But the GCC 6.3.1 compiler had generated this much shorter list (addresses 
differ because the compiler generated different code elsewhere):

    addr: 144C, line: 365, file: 5, stmt: True
    addr: 1456, line: 366, file: 5, stmt: True
    addr: 145A, line: 368, file: 5, stmt: True
    addr: 145E, line: 373, file: 5, stmt: True
    addr: 1466, line: 320, file: 5, stmt: True
    addr: 146C, line: 267, file: 15, stmt: True
FixDebugInfo patches the GCC 9.3.1 output to look like this (no repeats
of address or line number):

    addr: 134E, line: 365, file: 5, stmt: True
    addr: 1358, line: 366, file: 5, stmt: True
    addr: 135C, line: 368, file: 5, stmt: True
    addr: 1360, line: 373, file: 5, stmt: True
    addr: 1366, line: 374, file: 5, stmt: True
    addr: 1368, line: 320, file: 5, stmt: True
    addr: 136E, line: 267, file: 15, stmt: True
Now the Microchip Studio debugger disassembly has it right:

                if (ch >= 'A' && ch <= 'Z')
    0000134E 03.00                 movs r3, r0       
    00001350 41.3b                 subs r3, #65      
    00001352 db.b2                 uxtb r3, r3       
    00001354 19.2b                 cmp  r3, #25      
    00001356 01.d8                 bhi  #2       
                    ch += 'a' - 'A';
    00001358 20.30                 adds r0, #32      
    0000135A c0.b2                 uxtb r0, r0       
                switch (ch)
    0000135C 77.28                 cmp  r0, #119         
    0000135E 06.d1                 bne  #12      
                    DEBUG_PRINT("Triggering WDT\n");
    00001360 64.48                 ldr  r0, [pc, #400]       
    00001362 00.f0.d3.fb           bl   #1958        
                    for (;;);
    00001366 fe.e7                 b    #-4      
            iTmp &= ~ID_Right;
    00001368 05.22                 movs r2, #5       
    0000136A 93.43                 bics r3, r2       
    0000136C ab.e7                 b    #-170  
