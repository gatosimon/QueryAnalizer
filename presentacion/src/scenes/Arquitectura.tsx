import React from 'react';
import {useFrame} from '../useFrame';
import {AbsoluteFill, interpolate, spring, useVideoConfig} from 'remotion';
import {C, DUR, FONT, MONO} from '../theme';
import {Icon, IconName} from '../components/Icon';
import {Words} from '../components/Words';
import {SceneFade} from '../components/SceneFade';

type Capa = {
  titulo: string;
  detalle: string;
  chips: string[];
  top: number;
  height: number;
  color: string;
  icono: IconName;
};

// Capas de la aplicación, de arriba (lo que ve el usuario) hacia abajo (la base de datos).
const CAPAS: Capa[] = [
  {
    titulo: 'Interfaz',
    detalle: 'WPF · .NET Framework 4.5 (x86)',
    chips: ['ModernWpfUI', 'AvalonEdit', 'Modo claro / oscuro', 'Pestañas de consulta'],
    top: 196,
    height: 140,
    color: C.accent,
    icono: 'tools',
  },
  {
    titulo: 'Servicios',
    detalle: 'Una clase de servicio por herramienta',
    chips: [
      'IntelliSense',
      'Diseñador de tablas',
      'Comparador',
      'Transferencia de datos',
      'Limpiador de BD',
      'Documentador (.docx)',
      'Esquematizador (Draw.io)',
      'Backup / Restauración',
      'Excel · CSV · JSON',
    ],
    top: 382,
    height: 262,
    color: C.accent2,
    icono: 'table',
  },
  {
    titulo: 'Acceso a datos',
    detalle: 'CapiDL.dll — clase DataBase',
    chips: ['ODBC de 32 bits', 'Drivers SQLite y PostgreSQL embebidos'],
    top: 684,
    height: 140,
    color: C.ok,
    icono: 'link',
  },
  {
    titulo: 'Motores',
    detalle: 'Driver y conexión elegidos en cada consulta',
    chips: ['SQL Server', 'PostgreSQL', 'DB2', 'SQLite'],
    top: 862,
    height: 140,
    color: C.warn,
    icono: 'db',
  },
];

const APOYO: {titulo: string; texto: string; icono: IconName}[] = [
  {titulo: 'Configuración', texto: 'Conexiones, historial, consultas guardadas y temas en %AppData%.', icono: 'folder'},
  {titulo: 'Autoactualización', texto: 'AutoUpdater lee el manifiesto en GitHub Releases y reemplaza el .exe.', icono: 'download'},
  {titulo: 'Instalador', texto: 'Paquete MSI con acciones personalizadas para instalar los drivers.', icono: 'plug'},
];

const X = 130;
const ANCHO = 1130;

export const Arquitectura: React.FC = () => {
  const frame = useFrame();
  const {fps} = useVideoConfig();

  return (
    <SceneFade duration={DUR.arquitectura}>
      <AbsoluteFill style={{fontFamily: FONT}}>
        <div
          style={{
            position: 'absolute',
            left: X,
            top: 70,
            color: C.accent,
            fontSize: 28,
            fontWeight: 700,
            letterSpacing: 6,
            textTransform: 'uppercase',
          }}
        >
          Arquitectura
        </div>
        <div style={{position: 'absolute', left: X, top: 108}}>
          <Words text="Capas simples, un servicio por herramienta" size={60} start={4} stagger={3} accentWords={['servicio']} />
        </div>

        {/* conectores con paquetes de datos que bajan y suben */}
        <svg width={1920} height={1080} style={{position: 'absolute', inset: 0}}>
          {CAPAS.slice(0, -1).map((c, i) => {
            const y1 = c.top + c.height;
            const y2 = CAPAS[i + 1].top;
            const draw = interpolate(frame, [28 + i * 24, 52 + i * 24], [0, 1], {
              extrapolateLeft: 'clamp',
              extrapolateRight: 'clamp',
            });
            const cx = X + ANCHO / 2;
            const p = ((frame * 1.6 + i * 14) % 46) / 46;
            const q = 1 - (((frame * 1.6 + i * 14 + 23) % 46) / 46);
            return (
              <g key={c.titulo} opacity={draw}>
                <line x1={cx} y1={y1} x2={cx} y2={y2} stroke={C.border} strokeWidth={3} strokeDasharray="6 8" />
                <circle cx={cx - 14} cy={y1 + (y2 - y1) * p} r={6} fill={C.accent} />
                <circle cx={cx + 14} cy={y1 + (y2 - y1) * q} r={6} fill={C.ok} />
              </g>
            );
          })}
        </svg>

        {/* capas */}
        {CAPAS.map((c, i) => {
          const s = spring({frame: frame - 22 - i * 22, fps, config: {damping: 18}});
          // Resalta cada capa por turnos, de arriba hacia abajo, para guiar la lectura
          const ini = 150 + i * 50;
          const foco = interpolate(frame, [ini - 6, ini + 6, ini + 44, ini + 56], [0, 1, 1, 0], {
            extrapolateLeft: 'clamp',
            extrapolateRight: 'clamp',
          });
          return (
            <div
              key={c.titulo}
              style={{
                position: 'absolute',
                left: X,
                top: c.top,
                width: ANCHO,
                height: c.height,
                borderRadius: 26,
                background: 'rgba(26,33,48,0.9)',
                border: `1.5px solid ${foco > 0.5 ? c.color : C.border}`,
                borderLeft: `8px solid ${c.color}`,
                boxShadow: `0 0 ${60 * foco}px ${c.color}55`,
                padding: '22px 30px',
                boxSizing: 'border-box',
                opacity: s,
                transform: `translateX(${(1 - s) * -120 + foco * 12}px)`,
                display: 'flex',
                gap: 26,
              }}
            >
              <div
                style={{
                  width: 66,
                  height: 66,
                  flex: '0 0 66px',
                  borderRadius: 18,
                  background: `${c.color}22`,
                  border: `1.5px solid ${c.color}66`,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <Icon name={c.icono} size={38} color={c.color} />
              </div>
              <div style={{flex: 1}}>
                <div style={{display: 'flex', alignItems: 'baseline', gap: 18}}>
                  <span style={{fontSize: 36, fontWeight: 700, color: C.text}}>{c.titulo}</span>
                  <span style={{fontSize: 22, color: C.muted, fontFamily: MONO}}>{c.detalle}</span>
                </div>
                <div style={{display: 'flex', flexWrap: 'wrap', gap: 10, marginTop: 14}}>
                  {c.chips.map((chip, k) => {
                    const cs = spring({frame: frame - 40 - i * 22 - k * 3, fps, config: {damping: 16}});
                    return (
                      <span
                        key={chip}
                        style={{
                          padding: '7px 18px',
                          borderRadius: 999,
                          background: `${c.color}1a`,
                          border: `1.5px solid ${c.color}55`,
                          color: C.text,
                          fontSize: 22,
                          fontWeight: 600,
                          opacity: cs,
                          transform: `scale(${0.8 + cs * 0.2})`,
                        }}
                      >
                        {chip}
                      </span>
                    );
                  })}
                </div>
              </div>
            </div>
          );
        })}

        {/* apoyo */}
        <div style={{position: 'absolute', left: 1330, top: 200, width: 460}}>
          <div
            style={{
              color: C.muted,
              fontSize: 24,
              fontWeight: 700,
              letterSpacing: 4,
              textTransform: 'uppercase',
              marginBottom: 20,
              opacity: interpolate(frame, [96, 112], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}),
            }}
          >
            Alrededor
          </div>
          {APOYO.map((a, i) => {
            const s = spring({frame: frame - 100 - i * 14, fps, config: {damping: 18}});
            return (
              <div
                key={a.titulo}
                style={{
                  background: 'rgba(26,33,48,0.9)',
                  border: `1.5px solid ${C.border}`,
                  borderRadius: 24,
                  padding: '26px 28px',
                  marginBottom: 24,
                  opacity: s,
                  transform: `translateX(${(1 - s) * 90}px)`,
                }}
              >
                <div style={{display: 'flex', alignItems: 'center', gap: 16}}>
                  <Icon name={a.icono} size={36} color={C.accent} />
                  <span style={{fontSize: 33, fontWeight: 700, color: C.text}}>{a.titulo}</span>
                </div>
                <div style={{fontSize: 23, color: C.muted, marginTop: 12, lineHeight: 1.35}}>{a.texto}</div>
              </div>
            );
          })}
        </div>
      </AbsoluteFill>
    </SceneFade>
  );
};
