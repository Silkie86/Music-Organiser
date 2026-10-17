using MusicOrganiser.Core.Formats;

// Format Lab from a terminal: dotnet run --project tools/MusicOrganiser.FormatLab -- tree "My song.flp"
return FormatLabCommand.Run(args, Console.Out, Console.Error);
