import { Component, type ReactNode } from 'react'

// If any page throws while rendering, show the error instead of a blank
// white page (React unmounts everything on an uncaught render error).
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <div className="center-page">
        <div className="center-card">
          <span className="label">Something broke</span>
          <h2>This page crashed.</h2>
          <pre className="mono" style={{ whiteSpace: 'pre-wrap', fontSize: 13, margin: 0 }}>{this.state.error.message}</pre>
          <button className="btn primary" onClick={() => { window.location.href = '/' }}>Back to the homepage</button>
        </div>
      </div>
    )
  }
}
