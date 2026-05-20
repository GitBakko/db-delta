using System.CommandLine;
using DbDelta.Cli.Commands;

RootCommand root = new("DbDelta — open-source SQL Server schema compare and deployment tool")
{
    CompareCommand.Build()
};

return await root.Parse(args).InvokeAsync();
