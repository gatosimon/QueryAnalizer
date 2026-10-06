# Convenciones del proyecto QueryAnalyzer

## Stack
- WPF .NET Framework 4.5, x86, C#
- Acceso a datos: `CapiDL.dll` (clase `DataBase`, ODBC 32-bit)
- Conexiones: `ConexionesManager` + `ConfigManager` (persiste en `config.xml`)
- UI: ModernWpfUI 0.9.6 (net45) + AvalonEdit. Las DLLs de terceros van **embebidas** como recursos `Embedded.<nombre>.dll` y se cargan con `AssemblyResolve` en `App()` (`App.xaml.cs`); por eso el ZIP de update lleva solo el `.exe`. Toda DLL nueva debe agregarse igual (`<Private>False</Private>` + `EmbeddedResource` con `LogicalName`), si no el update no la entrega.
- Build: MSBuild `C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe`
  - `/p:Configuration=Release` (sin `/p:Platform` — la solución no tiene config x86 separada)

---

## Modo oscuro — OBLIGATORIO en toda ventana nueva

Toda ventana secundaria DEBE incluir estos tres elementos:

### 1. En el XAML — tema + estilos compartidos + estilo ModernWpf
```xml
<Window ...
        xmlns:ui="http://schemas.modernwpf.com/2019"
        Background="{DynamicResource BrushWindowBG}"
        Foreground="{DynamicResource BrushFG}"
        ui:WindowHelper.UseModernWindowStyle="True">
  <Window.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="ThemeLight.xaml"/>   <!-- slot [0]: lo reemplaza AplicarTemaActual -->
        <ResourceDictionary Source="Styles.xaml"/>       <!-- estilos base compartidos -->
        <ResourceDictionary Source="Controles.xaml"/>    <!-- botones compuestos, etc. -->
      </ResourceDictionary.MergedDictionaries>
      <!-- estilos locales usando DynamicResource -->
    </ResourceDictionary>
  </Window.Resources>
```
- `Styles.xaml` y `Controles.xaml` van en **cada ventana**, no en `App.xaml` (los `BasedOn` no resuelven diccionarios hermanos a nivel aplicación). `Icons.xaml` sí está en `App.xaml`.
- No redefinir estilos de TextBox/Button/ComboBox/etc. en la ventana: heredan de ModernWpf + `Styles.xaml`.

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
| `BrushDanger` / `BrushDangerBG`   | Errores, acciones destructivas (texto / fondo suave) |
| `BrushSuccess` / `BrushSuccessBG` | Éxito, estado correcto                |
| `BrushWarning` / `BrushWarningBG` | Advertencias                          |
| `BrushAccentFG`      | Texto sobre fondo de acento                |

Hay además brushes `Input*` y `ControlBorder(+Hover)` (fondos y bordes suaves de TextBox/ComboBox) que `AplicarFondosEntradas` en `MainWindow.xaml.cs` vuelca a los recursos de ModernWpf. Mirar `ThemeLight.xaml` para la lista completa.

**Al agregar o cambiar un brush:** editar `ThemeLight.xaml`, `ThemeDark.xaml` **y** los valores por defecto de `PreferenciasWindow.xaml.cs`, y subir `ThemeVersion` / `PreferenciasWindow.VersionTemas` (hoy 5); sin eso, quien ya tiene los temas en `%AppData%\QueryAnalyzer\Themes\` no recibe el cambio (se reemplazan dejando un `.bak`).

### Mensajes, scrollbars y cambio de tema
- **Mensajes:** usar siempre `MessageBox.Show(...)` tal cual (misma firma que WPF). Dentro del namespace `QueryAnalyzer` resuelve a `DialogoMensajeWindow.xaml(.cs)` (diálogo con el diseño y el tema de la app), no al cuadro clásico de Windows. **Nunca** `System.Windows.MessageBox` / `System.Windows.Forms.MessageBox` calificados. El actualizador tiene su propio `MessageBox` equivalente (`DialogoTema.cs`). Los diálogos del sistema (abrir/guardar archivo, carpeta) no se pueden reemplazar.
- **ScrollBars:** hay un único estilo implícito global en `ScrollBars.xaml` (fino, sin flechas, pulgar redondeado con `BrushFGMuted`), cargado en `App.xaml` después de ModernWpf. No definir otros ni usar barras clásicas; si un control trae su propia barra (WinForms en el actualizador) se reemplaza por uno propio (`NotasControl`).
- **Controles compartidos (`Controles.xaml`):** `CheckBox` y `RadioButton` tienen plantilla propia (caja de 18 px, texto centrado con la caja; no usar `Padding`/`MinHeight` para "alinearlos"), `GroupBox` es una tarjeta con título, `ListBox` lleva marco, y las grillas salen del estilo `DataGrid`/`DataGridCell`/`DataGridColumnHeader` de ese archivo (filas de 26 px, alternadas, encabezado `BrushHeaderBG`). **No** redefinir esos estilos en cada ventana: en la ventana solo van estilos de estado (p. ej. `DataGridRow` con `BasedOn="{StaticResource DefaultDataGridRowStyle}"`). En grillas: `ElementStyle="{StaticResource GridTexto}"` en las `DataGridTextColumn` (texto centrado verticalmente) y `ElementStyle="{StaticResource GridCheckBox}"` en las `DataGridCheckBoxColumn`; si un estilo local de `CheckBox` no lleva `BasedOn`, pierde el aspecto compartido. Listas con casillas: `DataGrid` con `DataGridTemplateColumn`, no `ListView`/`GridView`. Pestañas: `BottomTabControl` + `BottomTab` (`Styles.xaml`). Botones: tamaño estándar (sin `Height`/`FontSize`/`Padding` fijos) y `MinWidth` en vez de `Width`; íconos con `Path Style="{StaticResource Ic}"`, nunca emojis ni caracteres sueltos como íconos.
- **Pestañas (`TabItem`):** los estilos son `Focusable=False`; `App.xaml.cs` registra un handler de clase que las selecciona con el click (WPF solo selecciona por foco). No quitarlo.
- **Menús desplegables de botones:** abrirlos con `AbrirMenuDeBoton` (alterna abrir/cerrar; sin eso el click que lo cierra lo reabre).
- **Cambio claro/oscuro:** cada recurso que se cambia por separado en `Application.Resources` recorre todo el árbol (~0,2 s con 1500 tablas). Agrupar los cambios de recursos en UN `ResourceDictionary` y reemplazarlo de una vez (ver `AplicarFondosEntradas`), y no tocar `ThemeManager.AccentColor` en cada alternancia. `BtnToggleTema_Click` muestra el aviso `overlayTema` antes del trabajo pesado.

### Iconos y emojis
- **Sin emojis** en la UI, en los textos de `AyudaWindow` ni en los documentos exportados (HTML/Excel/etc.).
- Iconos vectoriales: geometrías en `Icons.xaml`, creación por código con `Iconos.Crear(clave)` (`Iconos.cs`); se tiñen con el `Foreground` del control, así que siguen el tema.
- **Colores fijos prohibidos**: usar los brushes de la paleta (incluidos `BrushDanger/Success/Warning`).

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
- ZIP de update: solo `QueryAnalyzer.exe`, porque las DLLs (CapiDL, ModernWpf, AvalonEdit, etc.) van embebidas en el exe (ver Stack). Si alguna DLL deja de estar embebida, el ZIP debe incluirla.
- El actualizador es un proyecto aparte (`C:\Users\ssnunez\source\repos\AutoUpdater`, su propio repo); `Resources\AutoUpdater.exe` de esta solución es una copia de su build Release. Sigue el tema claro/oscuro leyendo `TemaOscuro` de `%AppData%\<app>\config.xml`; si cambia su paleta, mantenerla alineada con `ThemeLight/ThemeDark.xaml`.
- Archivos a **NO incluir** en el ZIP: `conexiones.xml`, `update_marker.xml`, `.pdb`
- Flujo de publicación: compilar → armar ZIP → subir ZIP a GitHub Release → actualizar `version.xml` → subir `version.xml` como asset del release marcado como "latest"

---

## Git

- Git de escritura **solo** al ejecutar el flujo "subí todo" (commit + push). Fuera de ese flujo, solo lectura (`git log`, `git status`, `git diff`); nada de branch, reset, rebase, tag, etc.

### Flujo "subí todo"
1. Poner la versión en `Properties/AssemblyInfo.cs` (`AssemblyVersion` y `AssemblyFileVersion`) con el esquema **`YY.M.d.N`** (desde la 26.10.6.0), un número por posición y sin ceros a la izquierda:
   - `YY`: los dos últimos dígitos del año actual (2026 → `26`).
   - `M`: el mes actual, 1 o 2 dígitos (octubre → `10`, marzo → `3`).
   - `d`: el día actual, 1 o 2 dígitos (6 → `6`, 27 → `27`).
   - `N`: el incremental del día, 0 a 99. Empieza en `0` con la primera publicación del día y sube de a 1 en cada publicación posterior del mismo día; al cambiar el día vuelve a `0`.
   - Ejemplos: primera del 6/10/2026 → `26.10.6.0`; segunda del mismo día → `26.10.6.1`; primera del 7/10/2026 → `26.10.7.0`.
   - Para saber el incremental, mirar la versión actual de `AssemblyInfo.cs`: si ya tiene la fecha de hoy, sumar 1 al último número; si no, usar `0`.
2. Compilar Release (MSBuild, ver Stack). Si hay errores, cortar.
3. `git add -A` → `git commit` (título `FIX:`/`FEAT:` + notas) → `git push`.
4. Copiar `bin\Release\QueryAnalyzer.exe` a `C:\Users\ssnunez\Desktop\BORRADERO\QueryAnalyzerUpdates` (sobrescribe).
5. En esa carpeta, crear `update-X.X.X.X.zip` con solo `QueryAnalyzer.exe`.
6. Copiar `version.xml` → `version-X.X.X.X.xml`, completar `<Version>`, `<DownloadUrl>` (`https://github.com/gatosimon/QueryAnalyzerUpdates/releases/download/vX.X.X.X/update-X.X.X.X.zip`) y `<ReleaseNotes>` (notas en lenguaje simple separadas por `\n` literal, sin acentos). Guardarlo también como `version.xml` (UTF-8 sin BOM).
7. Publicar el Release en `gatosimon/QueryAnalyzerUpdates`: `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\ssnunez\.claude\scripts\qa_publicar_release.ps1" -Version X.X.X.X`. El script crea el tag `vX.X.X.X` desde main, con título "Versión X.X.X.X", las notas como descripción y los dos archivos adjuntos (zip y `version.xml`); lo marca como latest y verifica. Usa el token de Git Credential Manager y el proxy corporativo. Si el release ya existe, aborta. El modo automático de Claude Code bloquea este paso: hay que salir del modo automático para correrlo.
8. Devolver al usuario: la versión y las notas.
