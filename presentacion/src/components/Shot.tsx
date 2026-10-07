import React from 'react';
import {Img, staticFile} from 'remotion';
import {C} from '../theme';

type Props = {
  /** Archivo dentro de /public */
  src: string;
  width: number;
  height: number;
  style?: React.CSSProperties;
};

/** Captura real de la app dentro de un marco con esquinas redondeadas, sombra y borde luminoso. */
export const Shot: React.FC<Props> = ({src, width, height, style}) => (
  <div
    style={{
      width,
      height,
      borderRadius: 22,
      overflow: 'hidden',
      background: C.panel,
      border: `1.5px solid ${C.border}`,
      boxShadow: `0 40px 90px rgba(0,0,0,0.55), 0 0 0 1px rgba(76,194,255,0.10), 0 0 120px rgba(76,194,255,0.12)`,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      ...style,
    }}
  >
    <Img src={staticFile(src)} style={{width: '100%', height: '100%', objectFit: 'contain'}} />
  </div>
);
