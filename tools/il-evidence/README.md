# IL-level comparison of the shipped build against the current one

Purpose: settle whether the staged payload differs from the previous shipped build
beyond the intended changes, using evidence that does not depend on a decompiler's
reference resolution.

Why IL and not decompiled C#: `ilspycmd` renders the same code differently depending on
which references it manages to resolve, so a C#-text diff between two DLLs built in
different environments produces phantom "structural" differences (numeric enum casts,
`StringName.op_Implicit`, `Unknown result type` comments). Those are rendering artifacts.
The IL listing is reference-independent for everything that matters here, and branch
targets can be normalized away, leaving opcodes and operands.

## How this was produced

    ilspycmd SHIPPED-c0e149878b228f28.dll -il > il-shipped.txt
    ilspycmd <staged RegentFXFastBoot.dll> -il > il-new.txt

`SHIPPED-c0e149878b228f28.dll` is the previously published payload (version 0.2.0, the
bytes the Workshop served before this release).

## Comparison method

1. Split each listing into `.class` / `.method` blocks and key each method by
   `enclosing-class::name(args)`.
2. Compare the two key sets: methods present in only one listing.
3. For the common methods, normalize both bodies (drop `//` comments, drop `.` directives,
   drop `IL_xxxx:` labels, replace branch targets `IL_xxxx` with a placeholder) and compare
   the resulting opcode/operand sequences.

## Result

Method sets: 37 methods in the shipped build, 63 in the current one.

- Methods only in the shipped build: **0**.
- Methods only in the current build: **26**, all belonging to the three files added by
  RFX-3 (`LateOrderNotice`, `LateOrderNoticeWatcher`, `NoticeState`) plus their generated
  Godot property accessors and the compiler-generated backing members.

Normalized bodies: of the 37 common methods, **36 are byte-identical after
normalization**. The single differing method is `MainFile.Initialize()`, and its entire
difference is:

    - ldstr "<old LATE-ORDER text>"
    + ldstr "<new LATE-ORDER text>"
    - ldstr "."
    + ldstr ". A one-shot popup will explain this on the main menu (see NOTICE: lines)."
    + call void RegentFXFastBoot.RegentFXFastBootCode.LateOrderNoticeWatcher::Schedule()
    - ldstr "<old ARMED text>"
    + ldstr "<new ARMED text>"

i.e. exactly the two rewritten log messages and the one new call that schedules the
notice. No other common method changed in any way.

## What this establishes and what it does not

Establishes: the staged payload is the current source, and its behaviour differs from the
published 0.2.0 build only in the log text and the added notice. The earlier C#-text diff
that appeared to show structural changes was a decompiler artifact.

Does not establish: that the code is correct at runtime. Only an in-game launch can show
that, and it has not been performed for this release.

Note on a claim that does not hold: the two builds do not target different GodotSharp
builds. Both emit `SetGodotClassPropertyValue(valuetype godot_string_name&
modreq(InAttribute) ...)`, i.e. both use `in`, and both reference the same assembly set
(`GodotSharp`, `sts2`, `RegentFX`, `System.Runtime`, `0Harmony`). The rendering
difference came from the decompiler, not from the binaries.
