import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth'
import { Avatar } from './ui'

export function Layout() {
  const { me, loading, logout } = useAuth()
  const [open, setOpen] = useState(false)
  const menu = useRef<HTMLDivElement>(null)
  const location = useLocation()
  const navigate = useNavigate()

  // Close the user menu on navigation or an outside click.
  useEffect(() => { setOpen(false) }, [location.pathname])
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => { if (!menu.current?.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [open])
  // Braces matter: newer browsers make scrollTo() return a Promise, and an
  // effect may only return a cleanup function (or nothing).
  useEffect(() => { window.scrollTo(0, 0) }, [location.pathname])

  return (
    <>
      <header className="header">
        <div className="wrap">
          <Link to="/" className="logo"><span className="logo-mark" /><span className="logo-text">MYENGINE</span><span className="logo-sub">/games</span></Link>
          <nav className="nav">
            <NavLink to="/" end>Browse</NavLink>
            <NavLink to="/new">Post a game</NavLink>
          </nav>
          <span className="spacer" />
          {loading ? null : me ? (
            <div className="user-menu" ref={menu}>
              <button className="user-button" aria-expanded={open} onClick={() => setOpen(o => !o)}>
                <Avatar user={me} />
                <span className="user-name">{me.displayName}</span>
                <span aria-hidden>▾</span>
              </button>
              {open && (
                <div className="dropdown">
                  <Link to={`/u/${me.username}`}>Your profile &amp; games</Link>
                  <Link to="/new">Post a new game</Link>
                  <Link to="/settings">Settings</Link>
                  <hr />
                  <button onClick={async () => { await logout(); navigate('/') }}>Sign out</button>
                </div>
              )}
            </div>
          ) : (
            <Link className="btn small primary" to={`/login?next=${encodeURIComponent(location.pathname)}`}>Sign in</Link>
          )}
        </div>
      </header>
      <main><Outlet /></main>
      <footer className="footer">
        <div className="wrap">
          <span className="label">MyEngine Games — made with a from-scratch C# engine</span>
          <span className="label">Windows builds · .zip</span>
        </div>
      </footer>
    </>
  )
}
