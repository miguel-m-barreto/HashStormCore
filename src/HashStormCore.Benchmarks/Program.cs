using BenchmarkDotNet.Running;

var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);

if(args.Length == 0)
    switcher.RunAll();
else
    switcher.Run(args);
