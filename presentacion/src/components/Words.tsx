import React from 'react';
import {useFrame} from '../useFrame';
import {spring, useVideoConfig} from 'remotion';
import {C, FONT} from '../theme';

type Props = {
  text: string;
  size: number;
  /** Cuadro (relativo a la escena) en el que arranca la primera palabra. */
  start?: number;
  /** Cuadros entre una palabra y la siguiente. */
  stagger?: number;
  weight?: number;
  color?: string;
  /** Palabras que se pintan con el color de acento. */
  accentWords?: string[];
  align?: 'left' | 'center';
  style?: React.CSSProperties;
};

/** Texto que aparece palabra por palabra (resorte + desplazamiento vertical). */
export const Words: React.FC<Props> = ({
  text,
  size,
  start = 0,
  stagger = 3,
  weight = 700,
  color = C.text,
  accentWords = [],
  align = 'left',
  style,
}) => {
  const frame = useFrame();
  const {fps} = useVideoConfig();
  const words = text.split(' ');

  return (
    <div
      style={{
        fontFamily: FONT,
        fontSize: size,
        fontWeight: weight,
        lineHeight: 1.12,
        color,
        textAlign: align,
        letterSpacing: -size * 0.012,
        display: 'flex',
        flexWrap: 'wrap',
        justifyContent: align === 'center' ? 'center' : 'flex-start',
        columnGap: size * 0.28,
        ...style,
      }}
    >
      {words.map((w, i) => {
        const s = spring({frame: frame - start - i * stagger, fps, config: {damping: 200}});
        const isAccent = accentWords.some((a) => w.replace(/[.,:;!?]/g, '') === a);
        return (
          <span
            key={`${w}-${i}`}
            style={{
              display: 'inline-block',
              opacity: s,
              transform: `translateY(${(1 - s) * size * 0.55}px)`,
              color: isAccent ? C.accent : undefined,
            }}
          >
            {w}
          </span>
        );
      })}
    </div>
  );
};
