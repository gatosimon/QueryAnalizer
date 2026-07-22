# Convenciones del proyecto QueryAnalyzer

## Stack
- WPF .NET Framework 4.5, x86, C#
- Acceso a datos: `CapiDL.dll` (clase `DataBase`, ODBC 32-bit)
- Conexiones: `ConexionesManager` + `ConfigManager` (persiste en `config.xml`)
- Build: MSBuild `C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe`
  - `/p:Configuration=Release` (sin `/p:Platform` — la solución no tiene config x86 separada)

---

## Modo oscuro — OBLIGATORIO en toda ventana nueva

Toda ventana secundaria DEBE incluir estos tres elementos:

### 1. En el XAML — MergedDictionaries con ThemeLight como initial value
```xml
<Window.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="ThemeLight.xaml"/>
    </ResourceDictionary.MergedDictionaries>
    <!-- estilos locales usando DynamicResource -->
  </ResourceDictionary>
</Window.Resources>
```

### 2. En el code-behind — AplicarTemaActual() en el constructor
```csharp
public MiVentana()
{
    InitializeComponent();
    AplicarTemaActual();   // ← SIEMPRE antes de cualquier otra inicialización
    // resto del constructor...
}

private void AplicarTemaActual()
{
    var mainWindow = Application.Current.MainWindow;
    if (mainWindow == null) return;
    var tema = mainWindow.Resources.MergedDictionaries.FirstOrDefault();
    if (tema == null) return;
    var wd = Resources.MergedDictionaries;
    if (wd.Count > 0) wd[0] = tema;
    else wd.Add(tema);
}
```

### 3. En estilos XAML — usar SIEMPRE DynamicResource, nunca colores hardcodeados
```xml
Background="{DynamicResource BrushWindowBG}"
Foreground="{DynamicResource BrushFG}"
BorderBrush="{DynamicResource BrushBorder}"
```

**MainWindow propaga cambios de tema** a todas las ventanas abiertas via `Application.Current.Windows`. El slot `MergedDictionaries[0]` es el que se reemplaza.

---

## Brushes disponibles (ThemeLight.xaml / ThemeDark.xaml)

| Clave                | Uso principal                              |
|----------------------|--------------------------------------------|
| `BrushWindowBG`      | Fondo de ventana                           |
| `BrushPanelBG`       | Fondo de paneles/secciones                 |
| `BrushControlBG`     | Fondo de TextBox, ComboBox, ListBox, etc.  |
| `BrushAltRowBG`      | Fila alternada en DataGrid                 |
| `BrushBorder`        | Bordes de controles                        |
| `BrushSplitter`      | GridSplitter                               |
| `BrushFG`            | Texto principal                            |
| `BrushFGMuted`       | Texto secundario / notas                   |
| `BrushHover`         | Hover sobre elementos interactivos         |
| `BrushSelected`      | Fondo de selección                         |
| `BrushSelectedFG`    | Texto sobre selección                      |
| `BrushAccent`        | Color de acento (azul claro / azul oscuro) |
| `BrushHeaderBG`      | Encabezado de DataGrid                     |
| `BrushHeaderFG`      | Texto de encabezado                        |
| `BrushBtnBG`         | Fondo de botones                           |
| `BrushBtnBorder`     | Borde de botones                           |
| `BrushTreeHover`     | Hover en TreeView                          |
| `BrushTreeSel`       | Selección en TreeView                      |
| `BrushTabSelBG`      | Tab seleccionado fondo                     |
| `BrushTabSelBdr`     | Tab seleccionado borde                     |
| `BrushTabSelFG`      | Tab seleccionado texto                     |
| `BrushMenuBG`        | Fondo de menú contextual                   |
| `BrushMenuHover`     | Hover en menú contextual                   |
| `BrushSeparator`     | Separadores                                |
| `BrushEditor`        | Fondo del editor SQL (AvalonEdit)          |
| `BrushEditorFG`      | Texto del editor SQL                       |
| `BrushRowHover`      | Hover en fila de DataGrid                  |

---

## Ventanas — convenciones de apertura

| Tipo de ventana                        | Método    | StartupLocation  |
|----------------------------------------|-----------|------------------|
| Diálogos modales (guardar, exportar…)  | `ShowDialog()` | `CenterOwner` |
| Ventanas auxiliares (no bloquean)      | `Show()`  | `CenterOwner`    |
| `DatosConexion`                        | `ShowDialog()` | `CenterScreen` (excepción histórica) |

Siempre asignar `Owner = this` antes de abrir.

---

## Ayuda — OBLIGATORIO al agregar funcionalidad

Toda feature nueva debe tener su sección en `AyudaWindow.xaml`.

**Formato estándar** (estilos locales definidos en AyudaWindow.xaml):
```xml
<!-- ══ NOMBRE SECCIÓN ════════════════════════════════════════ -->
<TextBlock Style="{StaticResource Titulo}"    Text="🔤 Nombre de la Sección"/>
<TextBlock Style="{StaticResource Subtitulo}" Text="Subsección"/>
<TextBlock Style="{StaticResource Cuerpo}"    Text="• Descripción del comportamiento."/>
<TextBlock Style="{StaticResource Nota}"      Text="Tip: texto de tip o nota aclaratoria."/>
<TextBlock Style="{StaticResource Codigo}"    Text="ejemplo de código o atajo"/>
```

El contenido es XAML estático en `AyudaWindow.xaml`. No hay code-behind de contenido.
Agregar la sección nueva **antes** del bloque `<!-- ══ ACTUALIZACIONES ══ -->` (que siempre va último).

---

## Acceso a datos (CapiDL / DataBase)

```csharp
var DB = new DataBase(connStr);         // abre conexión ODBC
DB.CommandText = "SELECT ...";
while (DB.Read())
{
    string val = DB.Reader[0].ToString();
    bool esNull = DB.IsDBNull(1);
}
DB.CloseConnection();

// Para DataTable completa:
DataTable dt = DB.DataTable("SELECT ...");

// Para metadatos ODBC:
DataTable dt = DB.GetSchema("TABLEs");   // "VIEWs", "Columns", "Indexes"
```

---

## Proyecto (.csproj) — registrar archivos nuevos

Al crear un `.cs` nuevo agregar en el `<ItemGroup>` de `<Compile>`:
```xml
<Compile Include="MiArchivo.cs" />
```

Al crear un par `.xaml` + `.xaml.cs`:
```xml
<!-- En ItemGroup de Compile -->
<Compile Include="MiVentana.xaml.cs">
  <DependentUpon>MiVentana.xaml</DependentUpon>
</Compile>

<!-- En ItemGroup de Page -->
<Page Include="MiVentana.xaml">
  <SubType>Designer</SubType>
  <Generator>MSBuild:Compile</Generator>
</Page>
```

---

## AutoUpdater

- Manifest URL hardcodeada en `App.xaml.cs`: `https://github.com/gatosimon/QueryAnalyzerUpdates/releases/latest/download/version.xml`
- Versión local: `update_marker.xml` (campo `InstalledVersion`)
- Versión actual del ensamblado: `AssemblyInfo.cs` (`AssemblyVersion` / `AssemblyFileVersion`)
- ZIP de update: debe incluir **todas las DLLs** de `bin\Release\` (no solo el `.exe`)
- Archivos a **NO incluir** en el ZIP: `conexiones.xml`, `update_marker.xml`, `.pdb`
- Flujo de publicación: compilar → armar ZIP → subir ZIP a GitHub Release → actualizar `version.xml` → subir `version.xml` como asset del release marcado como "latest"

---

## Git

- **Prohibido cualquier comando git de escritura** (commit, push, branch, reset, etc.)
- Solo lectura si hace falta (`git log`, `git status`, `git diff`)
