using RtxAutoCurveOptimizer.Cli;

RtxAutoCurveOptimizer.Optimization.AppDataFolder.MigrateLegacy();
return await CommandLine.RunAsync(args);
