import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate, spring, useVideoConfig} from 'remotion';
import {C, DUR, FONT} from '../theme';
import {Icon, IconName} from '../components/Icon';
import {Words} from '../components/Words';
import {SceneFade} from '../components/SceneFade';

const DOLORES: {icono: IconName; titulo: string; texto: string}[] = [
  {
    icono: 'db',
    titulo: 'Una herramienta por motor',
    texto: 'SQL Server, PostgreSQL, DB2 y SQLite: cada base con su propio cliente y su propia forma de trabajar.',
  },
  {
    icono: 'plug',
    titulo: 'Drivers ODBC de 32 bits',
    texto: 'Instalarlos y configurarlos lleva tiempo y suele fallar de un equipo a otro.',
  },
  {
    icono: 'history',
    titulo: 'Tareas manuales y repetitivas',
    texto: 'Comparar esquemas, migrar datos o documentar tablas a mano consume horas.',
  },
  {
    icono: 'close',
    titulo: 'Cambios sin red de seguridad',
    texto: 'Modificar una base real sin un script para revisar antes de aplicar es un riesgo.',
  },
];

export const Problema: React.FC = () => {
  const frame = useFrame();
  const {fps} = useVideoConfig();

  const solucion = spring({frame: frame - 330, fps, config: {damping: 16}});
  // 1 mientras se resaltan las tarjetas por turnos, 0 antes y después
  const modoFoco = interpolate(frame, [92, 104], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}) *
    (1 - interpolate(frame, [322, 334], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));

  return (
    <SceneFade duration={DUR.problema}>
      <AbsoluteFill style={{fontFamily: FONT, padding: '110px 130px'}}>
        <div
          style={{
            color: C.danger,
            fontSize: 28,
            fontWeight: 700,
            letterSpacing: 6,
            textTransform: 'uppercase',
            opacity: interpolate(frame, [0, 14], [0, 1], {extrapolateRight: 'clamp'}),
          }}
        >
          El problema
        </div>
        <Words
          text="Trabajar con varias bases de datos no debería ser tan complicado"
          size={80}
          start={6}
          stagger={3}
          accentWords={['complicado']}
          style={{marginTop: 20, maxWidth: 1500}}
        />

        <div style={{display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 30, marginTop: 78}}>
          {DOLORES.map((d, i) => {
            const s = spring({frame: frame - 36 - i * 10, fps, config: {damping: 18}});
            // Resalta cada tarjeta por turnos (guía la lectura): 55 cuadros de animación cada una, desde el cuadro 100
            const ini = 100 + i * 55;
            const foco = interpolate(frame, [ini - 8, ini + 6, ini + 49, ini + 63], [0, 1, 1, 0], {
              extrapolateLeft: 'clamp',
              extrapolateRight: 'clamp',
            });
            return (
              <div
                key={d.titulo}
                style={{
                  background: 'rgba(26,33,48,0.85)',
                  border: `1.5px solid ${foco > 0.5 ? C.danger : C.border}`,
                  borderRadius: 26,
                  padding: '38px 34px',
                  minHeight: 330,
                  opacity: s * (1 - 0.4 * modoFoco * (1 - foco)),
                  transform: `translateY(${(1 - s) * 70 - foco * 14}px) scale(${1 + foco * 0.025})`,
                  boxShadow: `0 24px 60px rgba(255,138,148,${0.22 * foco})`,
                }}
              >
                <div
                  style={{
                    width: 76,
                    height: 76,
                    borderRadius: 20,
                    background: 'rgba(255,138,148,0.14)',
                    border: '1.5px solid rgba(255,138,148,0.35)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    marginBottom: 26,
                  }}
                >
                  <Icon name={d.icono} size={44} color={C.danger} />
                </div>
                <div style={{fontSize: 34, fontWeight: 700, color: C.text, lineHeight: 1.15}}>{d.titulo}</div>
                <div style={{fontSize: 25, color: C.muted, marginTop: 14, lineHeight: 1.35}}>{d.texto}</div>
              </div>
            );
          })}
        </div>

        {/* Puente hacia la solución */}
        <div
          style={{
            position: 'absolute',
            left: 130,
            right: 130,
            bottom: 82,
            display: 'flex',
            alignItems: 'center',
            gap: 24,
            opacity: solucion,
            transform: `translateX(${(1 - solucion) * -80}px)`,
          }}
        >
          <div style={{height: 3, width: 90, background: C.accent, borderRadius: 3}} />
          <div style={{fontSize: 42, fontWeight: 600, color: C.text}}>
            <span style={{color: C.accent}}>QueryAnalyzer</span> lo resuelve en una sola aplicación.
          </div>
        </div>
      </AbsoluteFill>
    </SceneFade>
  );
};
