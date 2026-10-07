# Presentación animada de QueryAnalyzer

Video de motion graphics (1920×1080, 30 fps, ~81 s, con música) hecho con [Remotion](https://www.remotion.dev) (React + TypeScript).

## Escenas

| # | Escena | Duración | Contenido |
|---|--------|----------|-----------|
| 1 | Portada | 8 s | Nombre, qué hace la app, motores soportados y captura real de la ventana principal |
| 2 | Problema | 18 s | Los cuatro dolores que resuelve: una herramienta por motor, drivers ODBC, tareas manuales, cambios sin revisar |
| 3 | Funcionalidades | 30 s | Flujo de uso (conectar → explorar → consultar → revisar → exportar) y las 5 herramientas principales con capturas reales |
| 4 | Arquitectura | 16 s | Capas (interfaz WPF → servicios → CapiDL/ODBC → motores) y piezas de apoyo (configuración, autoactualización, instalador) |
| 5 | Cierre | 9 s | Mensaje final y cifras |

Las duraciones (`SEGUNDOS`), el factor de lentitud (`SLOW`) y la paleta están en `src/theme.ts`; cada escena es un archivo en `src/scenes/`.
Para que el video vaya más rápido o más lento se cambian esos números (por ejemplo `SLOW = 1` es el ritmo original y `SEGUNDOS.problema` es cuánto tiempo queda la escena para leerla).
Las capturas están en `public/` (son de una base de prueba, sin datos reales).

## Música

La pista `public/musica.wav` es **original** (sintetizada por código en `tools/generar-musica.js`, sin samples ni material de terceros: no tiene derechos de autor). Es un tema suave de electrónica optimista, 88 BPM, de ~87 s.
Se puede volver a generar con `npm run musica`, o reemplazar por otra pista con el mismo nombre (`public/musica.wav`). El volumen y los fundidos se ajustan en `src/Presentacion.tsx` (`volumenMusica`).

## Requisitos

- Node.js 18 o superior. En esta PC el `node` activo es el 10, que no sirve: usar el 18 que está instalado con nvm
  (`C:\Users\ssnunez\AppData\Roaming\nvm\v18.20.7`).
- La primera vez Remotion descarga un Chrome headless (~110 MB).

## Comandos

Desde esta carpeta (`presentacion`), con Node 18 en el PATH:

```powershell
$env:PATH = 'C:\Users\ssnunez\AppData\Roaming\nvm\v18.20.7;' + $env:PATH

npm install            # solo la primera vez
npm run render         # genera out/QueryAnalyzer-presentacion.mp4  (~8 min)
```

O simplemente `render.cmd`, que hace lo anterior.

Otros comandos:

| Comando | Qué hace |
|---------|----------|
| `npm start` | Abre Remotion Studio para ver y ajustar la animación en vivo |
| `npm run render` | Renderiza el video completo (con música) a `out/QueryAnalyzer-presentacion.mp4` |
| `npm run musica` | Regenera `public/musica.wav` |
| `npm run render:gif` | Versión GIF liviana (mitad de tamaño, un cuadro de cada dos) |
| `npm run still` | Guarda un cuadro suelto en `out/portada.png` |
| `npm run typecheck` | Revisa los tipos de TypeScript |
