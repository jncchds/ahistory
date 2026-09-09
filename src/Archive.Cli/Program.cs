// Archive.Cli — headless entry point.
//
// This exists so the V1 acceptance criterion (a real export re-imports as a no-op) is
// provable without a UI, and so M1-M3 are demonstrable before Archive.Desktop exists.
// Commands arrive with their milestones: `init` in M1, `hash` in M2, `import` in M3.

Console.Error.WriteLine("ahistory — no commands implemented yet (M0 skeleton).");
return 1;
