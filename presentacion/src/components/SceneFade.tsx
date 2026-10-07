import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate} from 'remotion';
import {FADE} from '../theme';

/** Envuelve una escena con fundido de entrada y de salida (la duración se pasa explícita). */
export const SceneFade: React.FC<{duration: number; children: React.ReactNode}> = ({duration, children}) => {
  const frame = useFrame();
  const opacity = interpolate(frame, [0, FADE, duration - FADE, duration], [0, 1, 1, 0], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  return <AbsoluteFill style={{opacity}}>{children}</AbsoluteFill>;
};
