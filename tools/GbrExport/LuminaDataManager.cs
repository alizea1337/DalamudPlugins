using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace GbrExport;

/// <summary>Dalamud's data manager, backed by Lumina reading the game files directly (sheets only).</summary>
internal sealed class LuminaDataManager(GameData game) : IDataManager
{
    public ClientLanguage Language => ClientLanguage.English;
    public GameData GameData => game;
    public ExcelModule Excel => game.Excel;
    public bool HasModifiedGameDataFiles => false;

    public ExcelSheet<T> GetExcelSheet<T>(ClientLanguage? language = null, string? name = null) where T : struct, IExcelRow<T>
        => game.GetExcelSheet<T>(Lumina.Data.Language.English, name)!;

    public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(ClientLanguage? language = null, string? name = null) where T : struct, IExcelSubrow<T>
        => game.GetSubrowExcelSheet<T>(Lumina.Data.Language.English, name)!;

    public FileResource? GetFile(string path) => game.GetFile(path);
    public T? GetFile<T>(string path) where T : FileResource => game.GetFile<T>(path);
    public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken) where T : FileResource
        => Task.FromResult(game.GetFile<T>(path)!);
    public bool FileExists(string path) => game.FileExists(path);
}
