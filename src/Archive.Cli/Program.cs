using Archive.Cli;

// Headless entry point. This exists so the V1 acceptance criterion — a real export re-imports
// as a no-op — is provable without a UI, and so each storage milestone is demonstrable before
// Archive.Desktop exists. Commands arrive with their milestones: `hash` in M2, `import` in M3.

return Commands.Run(args);
