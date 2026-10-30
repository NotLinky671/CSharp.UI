Independent probe evidence (fast reviewer), 2026-10-29.
Provenance: external Temp probe with ProjectReference to current src/CSharp.UI (P01 worktree), AssemblyName CSharp.UI.Tests (existing friend assembly, no product IVT change).

Files:
- independent-probe-build.* / independent-probe-run.*: build+run raw (UTF-8), exits 0 / 1.
- independent-probe-summary.txt: PASS/FAIL scenarios and the nested-OnMount failure raw.
- independent-baseline-build.* / independent-baseline-class.*: targeted run at baseline 249ca96 worktree (removed after).

Baseline failure numbers (identical to current P01 run):
GalleryIdleAndBoundsTests.极窄视口有确定几何与溢出诊断
Button("Copy") 越过极窄视口：(32, 17565.747, 51.883 x 24.621), stack GalleryIdleAndBoundsTests.cs(108)
Differences only: current diagnostic path has "RootLayer / " prefix; [RL-08] max measure 541 vs baseline 539.
Conclusion: pre-existing limitation, not introduced by the root layer. No rerun performed beyond this targeted class.
