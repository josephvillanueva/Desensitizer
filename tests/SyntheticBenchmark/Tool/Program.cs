using SyntheticBenchmark;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage:\n  generate --seeds 1,2,3 --entities 60 --out DIR\n  score --truth truth.json --findings findings.json [--protected-dir DIR] [--out DIR]");
    return 0;
}
return args[0] switch
{
    "generate" => Cli.Generate(args),
    "score" => Cli.Score(args),
    "selftest" => Cli.SelfTest(),
    _ => throw new ArgumentException("Unknown command " + args[0]),
};
