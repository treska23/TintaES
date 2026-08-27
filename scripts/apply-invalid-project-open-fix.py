from pathlib import Path


def read_text(path: Path) -> tuple[str, str]:
    with path.open("r", encoding="utf-8", newline="") as handle:
        text = handle.read()
    eol = "\r\n" if "\r\n" in text else "\n"
    return text, eol


def write_text(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


project_path = Path("src/TintaES.Wpf/MainWindow.ProjectPersistence.cs")
text, eol = read_text(project_path)

old_load = """            TintaProjectManifest manifest = await Task.Run(() => ExtractTintaProject(projectPath, workspace));

            _comicPages.Clear();
""".replace("\n", eol)
new_load = """            (TintaProjectManifest? manifest, string? openError) = await Task.Run(
                () => ExtractTintaProject(projectPath, workspace));
            if (manifest is null)
            {
                AbandonEmptyDocumentAfterOpenFailure();
                MessageBox.Show(
                    this,
                    openError ?? "El archivo seleccionado no es un proyecto de TintaES válido.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetFooterStatus("No se pudo abrir el proyecto.", "#EE594B");
                return;
            }

            _comicPages.Clear();
""".replace("\n", eol)
if text.count(old_load) != 1:
    raise RuntimeError("LoadTintaProjectAsync marker changed; refusing to patch.")
text = text.replace(old_load, new_load)

method_start = "    private static TintaProjectManifest ExtractTintaProject(string projectPath, string workspace)"
method_end = eol + eol + "    private sealed class TintaProjectManifest"
start_index = text.find(method_start)
end_index = text.find(method_end, start_index)
if start_index < 0 or end_index < 0 or text.find(method_start, start_index + 1) >= 0:
    raise RuntimeError("ExtractTintaProject boundaries changed; refusing to patch.")

new_method = """    private static (TintaProjectManifest? Manifest, string? Error) ExtractTintaProject(
        string projectPath,
        string workspace)
    {
        try
        {
            using FileStream input = File.OpenRead(projectPath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? manifestEntry = archive.GetEntry("project.json");
            if (manifestEntry is null)
            {
                return (null, "El archivo seleccionado no es un proyecto de TintaES válido.");
            }

            TintaProjectManifest? manifest;
            using (Stream manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<TintaProjectManifest>(manifestStream, ProjectJsonOptions);
            }

            if (manifest is null)
            {
                return (null, "El proyecto seleccionado tiene un manifiesto vacío o dañado.");
            }

            string workspaceRoot = Path.GetFullPath(workspace) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)
                    || string.Equals(entry.FullName, "project.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string target = Path.GetFullPath(
                    Path.Combine(workspace, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, "El proyecto contiene una ruta no válida.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream entryStream = entry.Open();
                using FileStream output = File.Create(target);
                entryStream.CopyTo(output);
            }

            return (manifest, null);
        }
        catch (InvalidDataException)
        {
            return (null, "El archivo seleccionado no es un proyecto de TintaES válido o está dañado.");
        }
        catch (JsonException)
        {
            return (null, "El proyecto seleccionado está dañado y no se puede leer.");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "No hay permisos para leer el archivo seleccionado.");
        }
        catch (IOException)
        {
            return (null, "No se pudo leer el archivo seleccionado. Puede estar dañado, bloqueado o incompleto.");
        }
    }
""".replace("\n", eol)
text = text[:start_index] + new_method + text[end_index:]
write_text(project_path, text)

menu_path = Path("src/TintaES.Wpf/MainWindow.ClassicMenu.cs")
menu, menu_eol = read_text(menu_path)
old_filter = '            Filter = "Proyecto TintaES|*.tinta|Todos los archivos|*.*",'
new_filter = '            Filter = "Proyecto TintaES|*.tinta",'
if menu.count(old_filter) != 1:
    raise RuntimeError("Open-project filter marker changed; refusing to patch.")
menu = menu.replace(old_filter, new_filter)
write_text(menu_path, menu)
