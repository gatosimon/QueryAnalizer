import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate, spring, useVideoConfig} from 'remotion';
import {C, DUR, FONT} from '../theme';
import {Icon} from '../components/Icon';
import {Words} from '../components/Words';
import {SceneFade} from '../components/SceneFade';

const DATOS = [
  {valor: '4', texto: 'motores de base de datos'},
  {valor: '9', texto: 'herramientas integradas'},
  {valor: '2', texto: 'temas: claro y oscuro'},
  {valor: '1', texto: 'clic para actualizar'},
];

export const Cierre: React.FC = () => {
  const frame = useFrame();
  const {fps} = useVideoConfig();
  const logo = spring({frame, fps, config: {damping: 12, stiffness: 110}});
  const brillo = 0.55 + Math.sin(frame / 10) * 0.15;

  return (
    <SceneFade duration={DUR.cierre}>
      <AbsoluteFill style={{fontFamily: FONT, alignItems: 'center', justifyContent: 'center'}}>
        <div
          style={{
            width: 130,
            height: 130,
            borderRadius: 36,
            background: `linear-gradient(145deg, ${C.accent}, #2b7de9)`,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            transform: `scale(${logo}) rotate(${(1 - logo) * 30}deg)`,
            boxShadow: `0 0 ${90 * brillo}px rgba(76,194,255,${brillo})`,
            marginBottom: 44,
          }}
        >
          <Icon name="db" size={80} color="#06223a" stroke={1.5} />
        </div>

        <Words text="Todas tus bases de datos, en una sola ventana." size={92} align="center" start={8} stagger={4} accentWords={['una', 'sola', 'ventana.']} style={{maxWidth: 1500}} />

        <div style={{display: 'flex', gap: 34, marginTop: 70}}>
          {DATOS.map((d, i) => {
            const s = spring({frame: frame - 44 - i * 8, fps, config: {damping: 15}});
            return (
              <div
                key={d.texto}
                style={{
                  width: 330,
                  padding: '28px 24px',
                  textAlign: 'center',
                  borderRadius: 26,
                  background: 'rgba(26,33,48,0.9)',
                  border: `1.5px solid ${C.border}`,
                  opacity: s,
                  transform: `translateY(${(1 - s) * 50}px) scale(${0.9 + s * 0.1})`,
                }}
              >
                <div style={{fontSize: 84, fontWeight: 800, color: C.accent, lineHeight: 1}}>{d.valor}</div>
                <div style={{fontSize: 25, color: C.muted, marginTop: 10}}>{d.texto}</div>
              </div>
            );
          })}
        </div>

        <div
          style={{
            marginTop: 64,
            fontSize: 32,
            color: C.muted,
            opacity: interpolate(frame, [76, 96], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}),
          }}
        >
          <span style={{color: C.text, fontWeight: 700}}>QueryAnalyzer</span> · Versión 26.10.6.0
        </div>
      </AbsoluteFill>
    </SceneFade>
  );
};
