import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';
import {C, W, H} from '../theme';

/** Fondo animado: degradado, manchas de color que derivan lentamente y una grilla muy tenue. */
export const Background: React.FC = () => {
  const frame = useCurrentFrame();
  const t = frame / 30;

  const blob = (
    x: number,
    y: number,
    size: number,
    color: string,
    speed: number,
    phase: number,
  ): React.CSSProperties => ({
    position: 'absolute',
    left: x + Math.sin(t * speed + phase) * 90,
    top: y + Math.cos(t * speed * 0.8 + phase) * 70,
    width: size,
    height: size,
    borderRadius: '50%',
    background: color,
    filter: 'blur(140px)',
    opacity: 0.42,
  });

  return (
    <AbsoluteFill style={{background: `linear-gradient(160deg, ${C.bg} 0%, ${C.bg2} 100%)`}}>
      <div style={blob(-200, -260, 820, '#1b6fa3', 0.5, 0)} />
      <div style={blob(W - 640, H - 560, 760, '#3b3f9e', 0.4, 2)} />
      <div style={blob(W / 2 - 300, H / 2 - 420, 560, '#0f4f6b', 0.3, 4)} />
      <svg width={W} height={H} style={{position: 'absolute', inset: 0, opacity: 0.07}}>
        <defs>
          <pattern id="grid" width="64" height="64" patternUnits="userSpaceOnUse">
            <path d="M64 0H0V64" fill="none" stroke="#9fb6d6" strokeWidth="1" />
          </pattern>
        </defs>
        <rect width={W} height={H} fill="url(#grid)" />
      </svg>
    </AbsoluteFill>
  );
};
