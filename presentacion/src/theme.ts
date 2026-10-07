// Paleta y duraciones compartidas por todas las escenas.
// Los colores salen del modo oscuro de QueryAnalyzer (acento celeste sobre fondo grafito).

export const W = 1920;
export const H = 1080;
export const FPS = 30;

export const C = {
  bg: '#0d1117',
  bg2: '#141a24',
  panel: '#1a2130',
  panelHi: '#222c3f',
  border: '#2c3649',
  text: '#e8eef6',
  muted: '#8d9ab0',
  accent: '#4cc2ff',
  accent2: '#8aa4ff',
  ok: '#6ccb5f',
  warn: '#f5c451',
  danger: '#ff8a94',
} as const;

export const FONT =
  "'Segoe UI Variable Display', 'Segoe UI', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif";
export const MONO = "'Cascadia Mono', Consolas, 'Courier New', monospace";

/** Las animaciones se escribieron a "ritmo normal"; este factor las hace más lentas (1,35 = 35 % más despacio). */
export const SLOW = 1.35;

/** Duración real de cada escena, en segundos (incluye tiempo de lectura al final de cada una). */
export const SEGUNDOS = {
  portada: 8,
  problema: 18,
  funcionalidades: 30,
  arquitectura: 16,
  cierre: 9,
} as const;

/** Duración real de cada escena en cuadros (lo que usa la composición). */
export const REAL = {
  portada: SEGUNDOS.portada * FPS,
  problema: SEGUNDOS.problema * FPS,
  funcionalidades: SEGUNDOS.funcionalidades * FPS,
  arquitectura: SEGUNDOS.arquitectura * FPS,
  cierre: SEGUNDOS.cierre * FPS,
} as const;

/** Duración de cada escena medida en "cuadros de animación" (cuadros reales / SLOW), que es lo que ven las escenas. */
export const DUR = {
  portada: Math.round(REAL.portada / SLOW),
  problema: Math.round(REAL.problema / SLOW),
  funcionalidades: Math.round(REAL.funcionalidades / SLOW),
  arquitectura: Math.round(REAL.arquitectura / SLOW),
  cierre: Math.round(REAL.cierre / SLOW),
} as const;

export const TOTAL = Object.values(REAL).reduce((a, b) => a + b, 0);

/** Cuadros de animación de fundido al entrar y salir de cada escena. */
export const FADE = 18;