import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate, spring, useVideoConfig} from 'remotion';
import {C, DUR, FONT} from '../theme';
import {Icon} from '../components/Icon';
import {Shot} from '../components/Shot';
import {Words} from '../components/Words';
import {SceneFade} from '../components/SceneFade';

const MOTORES = ['SQL Server', 'PostgreSQL', 'DB2', 'SQLite'];

export const Portada: React.FC = () => {
  const frame = useFrame();
  const {fps} = useVideoConfig();

  const logo = spring({frame, fps, config: {damping: 14, stiffness: 120}});
  const pulse = 1 + Math.sin(frame / 12) * 0.025;
  const shot = spring({frame: frame - 18, fps, config: {damping: 200}});
  const float = Math.sin(frame / 22) * 10;

  return (
    <SceneFade duration={DUR.portada}>
      <AbsoluteFill style={{fontFamily: FONT}}>
        {/* Texto */}
        <div style={{position: 'absolute', left: 130, top: 170, width: 1000}}>
          <div style={{display: 'flex', alignItems: 'center', gap: 22, marginBottom: 34}}>
            <div
              style={{
                width: 96,
                height: 96,
                borderRadius: 26,
                background: `linear-gradient(145deg, ${C.accent}, #2b7de9)`,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                transform: `scale(${logo * pulse}) rotate(${(1 - logo) * -25}deg)`,
                boxShadow: '0 20px 60px rgba(76,194,255,0.45)',
              }}
            >
              <Icon name="db" size={58} color="#06223a" stroke={1.5} />
            </div>
            <div
              style={{
                fontSize: 28,
                fontWeight: 600,
                color: C.accent,
                letterSpacing: 6,
                textTransform: 'uppercase',
                opacity: interpolate(frame, [8, 26], [0, 1], {extrapolateRight: 'clamp'}),
              }}
            >
              Analizador de consultas ODBC
            </div>
          </div>

          <div style={{display: 'flex', fontSize: 132, fontWeight: 800, letterSpacing: -2, lineHeight: 1.05, whiteSpace: 'nowrap'}}>
            {['Query', 'Analyzer'].map((w, i) => {
              const s = spring({frame: frame - 10 - i * 8, fps, config: {damping: 200}});
              return (
                <span key={w} style={{display: 'inline-block', opacity: s, transform: `translateY(${(1 - s) * 70}px)`, color: i === 1 ? C.accent : C.text}}>
                  {w}
                </span>
              );
            })}
          </div>
          <Words
            text="Consultá, compará, migrá y documentá tus bases de datos desde una sola ventana."
            size={42}
            weight={400}
            color={C.muted}
            start={34}
            stagger={2}
            style={{marginTop: 30, maxWidth: 800}}
          />

          <div style={{display: 'flex', gap: 14, marginTop: 52, flexWrap: 'wrap'}}>
            {MOTORES.map((m, i) => {
              const s = spring({frame: frame - 66 - i * 6, fps, config: {damping: 15}});
              return (
                <div
                  key={m}
                  style={{
                    padding: '12px 26px',
                    borderRadius: 999,
                    border: `1.5px solid ${C.border}`,
                    background: 'rgba(26,33,48,0.8)',
                    color: C.text,
                    fontSize: 26,
                    fontWeight: 600,
                    opacity: s,
                    transform: `translateY(${(1 - s) * 30}px) scale(${0.85 + s * 0.15})`,
                  }}
                >
                  {m}
                </div>
              );
            })}
          </div>
        </div>

        {/* Captura real de la app */}
        <div
          style={{
            position: 'absolute',
            right: -250,
            top: 300,
            opacity: shot,
            transform: `perspective(2200px) rotateY(${-14 + (1 - shot) * 22}deg) rotateX(4deg) translate(${(1 - shot) * 220}px, ${float}px)`,
            transformOrigin: 'left center',
          }}
        >
          <Shot src="main.png" width={1060} height={570} />
        </div>

        {/* Versión */}
        <div
          style={{
            position: 'absolute',
            left: 130,
            bottom: 70,
            color: C.muted,
            fontSize: 24,
            opacity: interpolate(frame, [70, 92], [0, 1], {extrapolateRight: 'clamp'}),
          }}
        >
          WPF · .NET Framework 4.5 · Versión 26.10.6.0
        </div>
      </AbsoluteFill>
    </SceneFade>
  );
};
