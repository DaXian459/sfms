/** SCADA 画面 JSON schema（与后端 SeedData / ScreensController 保持一致）。 */

export interface ScadaBinding {
  prop: string
  tagId: number
  mode?: 'direct' | 'expr'
  format?: string
  unit?: string
  label?: string
}

export interface StateMapItem { op: string; value: number | string; result: string }

export interface ScadaAnimation {
  kind: 'stateMap' | 'linear' | 'blink' | 'quality'
  prop?: string
  tagId: number
  default?: string
  map?: StateMapItem[]
  min?: number
  max?: number
  from?: number
  to?: number
  when?: number
  periodMs?: number
}

export interface ScadaEvent {
  kind: string
  action: string
  target?: string
}

export interface ScadaWidget {
  id: string
  type: string
  layerId?: string
  x: number
  y: number
  w: number
  h: number
  rotation?: number
  props: Record<string, any>
  bindings?: ScadaBinding[]
  animations?: ScadaAnimation[]
  events?: ScadaEvent[]
}

export interface ScadaLayer {
  id: string
  name: string
  visible?: boolean
  locked?: boolean
  z?: number
}

export interface ScadaDoc {
  schemaVersion: number
  code: string
  name: string
  width: number
  height: number
  background?: string
  layers?: ScadaLayer[]
  widgets: ScadaWidget[]
}

export interface RtValue { value?: number; text?: string; quality: number; ts?: string }

export const WIDGET_LABEL: Record<string, string> = {
  rect: '矩形', circle: '圆形', line: '直线', polyline: '折线', text: '文本',
  image: '图片', lamp: '指示灯', tank: '储罐', pipe: '管道', pump: '泵',
  fan: '风机', valve: '阀门', motor: '电机', gauge: '仪表'
}

/** 新建图元时的默认 props。 */
export function defaultProps(type: string): Record<string, any> {
  switch (type) {
    case 'rect': return { fill: '#101d2e', stroke: '#23384f', strokeWidth: 1.5, radius: 8 }
    case 'circle': return { fill: '#132538', stroke: '#2b4a63', strokeWidth: 1.5 }
    case 'line': return { stroke: '#2b4a63', strokeWidth: 3 }
    case 'polyline': return { stroke: '#2b4a63', strokeWidth: 3, points: '0,40 60,0 120,40' }
    case 'text': return { text: '文本', fontSize: 18, color: '#e2e8f0', bold: false, align: 'left' }
    case 'image': return { href: '', fit: 'contain' }
    case 'lamp': return { offColor: '#f59e0b', onColor: '#16a34a', shape: 'circle' }
    case 'tank': return { fill: '#132538', levelColor: '#1d4ed8', min: 0, max: 100 }
    case 'pipe': return { stroke: '#2b4a63', strokeWidth: 14 }
    case 'pump': return { fill: '#1f6feb' }
    case 'fan': return { fill: '#1f6feb' }
    case 'valve': return { fill: '#1f6feb' }
    case 'motor': return { fill: '#1f6feb' }
    case 'gauge': return { min: 0, max: 100, color: '#38bdf8', unit: '' }
    default: return {}
  }
}

/** 新图元的默认尺寸。 */
export function defaultSize(type: string): { w: number; h: number } {
  switch (type) {
    case 'text': return { w: 200, h: 32 }
    case 'rect': return { w: 240, h: 120 }
    case 'circle': return { w: 80, h: 80 }
    case 'line': return { w: 160, h: 8 }
    case 'polyline': return { w: 160, h: 60 }
    case 'image': return { w: 200, h: 140 }
    case 'lamp': return { w: 40, h: 40 }
    case 'tank': return { w: 160, h: 200 }
    case 'pipe': return { w: 160, h: 14 }
    case 'pump': case 'fan': case 'motor': return { w: 90, h: 90 }
    case 'valve': return { w: 100, h: 76 }
    case 'gauge': return { w: 240, h: 220 }
    default: return { w: 120, h: 80 }
  }
}

export function emptyDoc(code: string, name: string, width = 1920, height = 1080): ScadaDoc {
  return {
    schemaVersion: 1, code, name, width, height, background: '#0b1220',
    layers: [{ id: 'L1', name: '背景层', visible: true, locked: false, z: 1 }],
    widgets: []
  }
}
