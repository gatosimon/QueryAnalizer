import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate, spring, useVideoConfig} from 'remotion';
import {C, DUR, FONT} from '../theme';
import {Icon, IconName} from '../components/Icon';
import {Shot} from '../components/Shot';
import {Words} from '../components/Words';
import {SceneFade} from '../components/SceneFade';

/** Flujo típico de uso, de izquierda a derecha. */
const PASOS: {icono: IconName; titulo: string; texto: string}[] = [
  {icono: 'plug', titulo: 'Conectar', texto: 'Elegí el motor y una conexión guardada.'},
  {icono: 'folder', titulo: 'Explorar', texto: 'Recorré tablas, vistas, columnas e índices.'},
  {icono: 'play', titulo: 'Consultar', texto: 'Escribí SQL y ejecutá con F5.'},
  {icono: 'table', titulo: 'Revisar', texto: 'Resultados en pestañas, editables.'},
  {icono: 'download', titulo: 'Exportar', texto: 'Excel, CSV, JSON o scripts INSERT.'},
];

/** Herramientas principales, cada una con su captura real. */
const FUNCIONES: {icono: IconName; titulo: string; texto: string; img: string}[] = [
  {
    icono: 'search',
    titulo: 'Consultas con pestañas',
    texto: 'Editor SQL con resaltado e IntelliSense, parámetros con ? y varios resultados a la vez.',
    img: 'main.png',
  },
  {
    icono: 'design',
    titulo: 'Diseñador de tablas',
    texto: 'Agregá, cambiá o quitá columnas y revisá el script DDL antes de aplicarlo.',
    img: 'disenador.png',
  },
  {
    icono: 'compare',
    titulo: 'Comparador de bases',
    texto: 'Detectá diferencias de tablas, vistas, índices y datos entre dos bases.',
    img: 'comparador.png',
  },
  {
    icono: 'transfer',
    titulo: 'Transferencia de datos',
    texto: 'Pasá tablas de una base a otra con backup previo y transacción.',
    img: 'transferencia.png',
  },
  {
    icono: 'broom',
    titulo: 'Limpiador de BD',
    texto: 'Analizá bajas lógicas y relaciones truncadas y generá el script de depuración.',
    img: 'limpiador.png',
  },
];

const TAMBIEN = ['Documentar (.docx)', 'Esquematizar (Draw.io)', 'Backup y restauración', 'Modo claro y oscuro', 'Autoactualización'];

const FASE_FLUJO = 150; // cuadros de animación que dura el flujo antes de pasar a las herramientas
const POR_FUNCION = 96; // cuadros de animación que ocupa cada herramienta (tiempo para leerla)

export const Funcionalidades: React.FC = () => {
  const frame = useFrame();
  const {fps} = useVideoConfig();

  // ── Fase A: flujo ───────────────────────────────────────────────
  const aOpacity = interpolate(frame, [FASE_FLUJO - 14, FASE_FLUJO], [1, 0], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  const linea = interpolate(frame, [18, 78], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});

  // ── Fase B: herramientas ────────────────────────────────────────
  const bOpacity = interpolate(frame, [FASE_FLUJO - 4, FASE_FLUJO + 14], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  const activa = Math.max(0, Math.min(FUNCIONES.length - 1, Math.floor((frame - FASE_FLUJO) / POR_FUNCION)));
  const tambien = interpolate(frame, [DUR.funcionalidades - 92, DUR.funcionalidades - 70], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });

  return (
    <SceneFade duration={DUR.funcionalidades}>
      <AbsoluteFill style={{fontFamily: FONT}}>
        {/* ───────── Fase A ───────── */}
        <AbsoluteFill style={{opacity: aOpacity, padding: '110px 130px'}}>
          <div style={{color: C.accent, fontSize: 28, fontWeight: 700, letterSpacing: 6, textTransform: 'uppercase'}}>
            Funcionalidades
          </div>
          <Words
            text="Del primer clic al resultado exportado"
            size={84}
            start={4}
            stagger={3}
            accentWords={['exportado']}
            style={{marginTop: 20}}
          />

          <div style={{position: 'relative', marginTop: 230}}>
            {/* línea de progreso */}
            <div style={{position: 'absolute', left: 80, right: 80, top: 56, height: 4, background: C.border, borderRadius: 4}} />
            <div
              style={{
                position: 'absolute',
                left: 80,
                top: 56,
                height: 4,
                width: `calc((100% - 160px) * ${linea})`,
                background: `linear-gradient(90deg, ${C.accent}, ${C.accent2})`,
                borderRadius: 4,
                boxShadow: `0 0 24px ${C.accent}`,
              }}
            />
            <div style={{display: 'grid', gridTemplateColumns: 'repeat(5, 1fr)', gap: 20}}>
              {PASOS.map((p, i) => {
                const s = spring({frame: frame - 16 - i * 12, fps, config: {damping: 14}});
                return (
                  <div key={p.titulo} style={{textAlign: 'center', opacity: s, transform: `translateY(${(1 - s) * 50}px)`}}>
                    <div
                      style={{
                        width: 116,
                        height: 116,
                        margin: '0 auto',
                        borderRadius: '50%',
                        background: C.panel,
                        border: `2px solid ${C.accent}`,
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        transform: `scale(${0.7 + s * 0.3})`,
                        boxShadow: '0 0 50px rgba(76,194,255,0.25)',
                      }}
                    >
                      <Icon name={p.icono} size={58} color={C.accent} />
                    </div>
                    <div style={{fontSize: 24, color: C.muted, fontWeight: 600, marginTop: 22}}>PASO {i + 1}</div>
                    <div style={{fontSize: 44, fontWeight: 700, color: C.text, marginTop: 4}}>{p.titulo}</div>
                    <div style={{fontSize: 25, color: C.muted, marginTop: 10, lineHeight: 1.3, padding: '0 14px'}}>{p.texto}</div>
                  </div>
                );
              })}
            </div>
          </div>
        </AbsoluteFill>

        {/* ───────── Fase B ───────── */}
        <AbsoluteFill style={{opacity: bOpacity}}>
          <div
            style={{
              position: 'absolute',
              left: 130,
              top: 96,
              color: C.accent,
              fontSize: 28,
              fontWeight: 700,
              letterSpacing: 6,
              textTransform: 'uppercase',
            }}
          >
            Herramientas
          </div>

          {/* lista */}
          <div style={{position: 'absolute', left: 130, top: 160, width: 700}}>
            {FUNCIONES.map((f, i) => {
              const on = i === activa;
              return (
                <div
                  key={f.titulo}
                  style={{
                    display: 'flex',
                    gap: 24,
                    padding: '20px 24px',
                    marginBottom: 10,
                    borderRadius: 22,
                    background: on ? 'rgba(76,194,255,0.12)' : 'transparent',
                    border: `1.5px solid ${on ? 'rgba(76,194,255,0.55)' : 'transparent'}`,
                    opacity: on ? 1 : 0.5,
                    transform: `translateX(${on ? 14 : 0}px)`,
                    transition: 'none',
                  }}
                >
                  <div
                    style={{
                      width: 68,
                      height: 68,
                      flex: '0 0 68px',
                      borderRadius: 18,
                      background: on ? C.accent : C.panel,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                    }}
                  >
                    <Icon name={f.icono} size={40} color={on ? '#06223a' : C.muted} stroke={1.4} />
                  </div>
                  <div>
                    <div style={{fontSize: 36, fontWeight: 700, color: C.text}}>{f.titulo}</div>
                    <div style={{fontSize: 23, color: C.muted, marginTop: 6, lineHeight: 1.3, display: on ? 'block' : 'none'}}>
                      {f.texto}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>

          {/* capturas */}
          <div style={{position: 'absolute', left: 880, top: 130, width: 910, height: 720}}>
            {FUNCIONES.map((f, i) => {
              const local = frame - (FASE_FLUJO + i * POR_FUNCION);
              const op = interpolate(local, [-8, 6, POR_FUNCION - 6, POR_FUNCION + 6], [0, 1, 1, i === FUNCIONES.length - 1 ? 1 : 0], {
                extrapolateLeft: 'clamp',
                extrapolateRight: 'clamp',
              });
              const enter = spring({frame: local, fps, config: {damping: 200}});
              return (
                <div
                  key={f.img}
                  style={{
                    position: 'absolute',
                    inset: 0,
                    opacity: op,
                    transform: `translateX(${(1 - Math.min(1, Math.max(0, enter))) * 70}px) scale(${0.96 + 0.04 * Math.min(1, Math.max(0, enter))})`,
                  }}
                >
                  <Shot src={f.img} width={910} height={720} />
                </div>
              );
            })}
          </div>

          {/* también */}
          <div
            style={{
              position: 'absolute',
              left: 130,
              right: 130,
              bottom: 62,
              display: 'flex',
              alignItems: 'center',
              gap: 14,
              flexWrap: 'wrap',
              opacity: tambien,
              transform: `translateY(${(1 - tambien) * 30}px)`,
            }}
          >
            <span style={{color: C.muted, fontSize: 26, fontWeight: 600, marginRight: 10}}>También:</span>
            {TAMBIEN.map((t) => (
              <span
                key={t}
                style={{
                  padding: '10px 22px',
                  borderRadius: 999,
                  border: `1.5px solid ${C.border}`,
                  background: 'rgba(26,33,48,0.9)',
                  color: C.text,
                  fontSize: 24,
                  fontWeight: 600,
                }}
              >
                {t}
              </span>
            ))}
          </div>
        </AbsoluteFill>
      </AbsoluteFill>
    </SceneFade>
  );
};
