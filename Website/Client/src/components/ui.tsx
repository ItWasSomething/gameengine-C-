import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { formatCount, type GameCard, type UserSummary } from '../api'

export function Avatar({ user, size }: { user: UserSummary; size?: 'small' | 'big' }) {
  const cls = `avatar${size ? ' ' + size : ''}`
  return user.avatarUrl
    ? <img className={cls} src={user.avatarUrl} alt="" />
    : <span className={cls} aria-hidden>{user.displayName.slice(0, 1)}</span>
}

export function GameCardView({ game }: { game: GameCard }) {
  return (
    <Link to={`/games/${game.slug}`} className="card">
      <div className="cover">
        {game.coverUrl ? <img src={game.coverUrl} alt="" loading="lazy" /> : <div className="cover-empty"><b>NO IMAGE</b></div>}
        {game.latestVersion && <span className="version-badge">v{game.latestVersion}</span>}
      </div>
      <div className="card-body">
        <h3>{game.title}</h3>
        {game.tagline && <p>{game.tagline}</p>}
      </div>
      <div className="card-foot">
        <Avatar user={game.author} size="small" />
        <span className="by">{game.author.displayName}</span>
        <span className="spacer" />
        <span title="Likes">♥ {formatCount(game.likes)}</span>
        <span title="Downloads">↓ {formatCount(game.downloads)}</span>
      </div>
    </Link>
  )
}

export function GridSkeleton({ count = 6 }: { count?: number }) {
  return (
    <div className="grid">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="card">
          <div className="cover skeleton" />
          <div className="card-body"><div className="skeleton" style={{ height: 20, width: '60%' }} /><div className="skeleton" style={{ height: 14 }} /></div>
        </div>
      ))}
    </div>
  )
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <div className="glyph" />
      <h3>{title}</h3>
      {children}
    </div>
  )
}

// Full-screen image viewer: click/Escape closes, arrow keys step.
export function Lightbox({ images, index, onClose, onIndex }: { images: string[]; index: number; onClose: () => void; onIndex: (i: number) => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowRight') onIndex((index + 1) % images.length)
      if (e.key === 'ArrowLeft') onIndex((index - 1 + images.length) % images.length)
    }
    window.addEventListener('keydown', onKey)
    document.body.style.overflow = 'hidden'
    return () => { window.removeEventListener('keydown', onKey); document.body.style.overflow = '' }
  }, [index, images.length, onClose, onIndex])

  const step = (d: number) => (e: React.MouseEvent) => { e.stopPropagation(); onIndex((index + d + images.length) % images.length) }
  return (
    <div className="lightbox" onClick={onClose} role="dialog" aria-label="Screenshot viewer">
      <span className="count label" style={{ color: '#fff' }}>{index + 1} / {images.length}</span>
      <img src={images[index]} alt="" onClick={e => e.stopPropagation()} />
      {images.length > 1 && <>
        <button className="nav-btn prev" onClick={step(-1)} aria-label="Previous">←</button>
        <button className="nav-btn next" onClick={step(1)} aria-label="Next">→</button>
      </>}
    </div>
  )
}

// A click-or-drop file picker.
export function DropZone({ accept, multiple, title, hint, onFiles }: {
  accept: string; multiple?: boolean; title: string; hint: string; onFiles: (files: File[]) => void
}) {
  const input = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  return (
    <label
      className={`drop${over ? ' over' : ''}`}
      onDragOver={e => { e.preventDefault(); setOver(true) }}
      onDragLeave={() => setOver(false)}
      onDrop={e => { e.preventDefault(); setOver(false); onFiles(Array.from(e.dataTransfer.files)) }}
    >
      <input ref={input} type="file" accept={accept} multiple={multiple}
        onChange={e => { onFiles(Array.from(e.target.files ?? [])); e.target.value = '' }} />
      <strong>{title}</strong>
      <span className="muted">{hint}</span>
    </label>
  )
}

export function Progress({ value }: { value: number }) {
  return <div className="progress" role="progressbar" aria-valuenow={Math.round(value * 100)}><div style={{ width: `${value * 100}%` }} /></div>
}

// Comma-separated tag entry that shows chips as you go.
export function TagInput({ value, onChange }: { value: string[]; onChange: (tags: string[]) => void }) {
  const [draft, setDraft] = useState('')
  const commit = useCallback((raw: string) => {
    const t = raw.trim().toLowerCase().replace(/\s+/g, '-').replace(/[^a-z0-9-]/g, '')
    if (t && !value.includes(t) && value.length < 6) onChange([...value, t])
    setDraft('')
  }, [value, onChange])
  return (
    <div className="stack" style={{ gap: 10 }}>
      <input className="input" value={draft} placeholder={value.length >= 6 ? 'Max 6 tags' : 'platformer, pixel-art… (Enter to add)'}
        disabled={value.length >= 6}
        onChange={e => e.target.value.endsWith(',') ? commit(e.target.value.slice(0, -1)) : setDraft(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'Enter') { e.preventDefault(); commit(draft) }
          if (e.key === 'Backspace' && !draft && value.length) onChange(value.slice(0, -1))
        }}
        onBlur={() => draft && commit(draft)} />
      {value.length > 0 && (
        <div className="tags">
          {value.map(t => <button type="button" key={t} className="tag on" onClick={() => onChange(value.filter(x => x !== t))}>{t} ✕</button>)}
        </div>
      )}
    </div>
  )
}

export function useToast(): [ReactNode, (msg: string) => void] {
  const [msg, setMsg] = useState<string | null>(null)
  useEffect(() => {
    if (!msg) return
    const t = setTimeout(() => setMsg(null), 2600)
    return () => clearTimeout(t)
  }, [msg])
  return [msg ? <div className="toast" role="status">{msg}</div> : null, setMsg]
}

export function useTitle(title: string) {
  useEffect(() => { document.title = title ? `${title} — MyEngine Games` : 'MyEngine Games' }, [title])
}
