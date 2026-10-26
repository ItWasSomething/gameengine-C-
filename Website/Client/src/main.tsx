import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { AuthProvider } from './auth'
import { ErrorBoundary } from './components/ErrorBoundary'
import { Layout } from './components/Layout'
import { DevicePage, LoginPage, NotFound, ProfilePage, SettingsPage } from './pages/Account'
import { EditGame } from './pages/EditGame'
import { GamePage } from './pages/GamePage'
import { Home } from './pages/Home'
import { NewGame } from './pages/NewGame'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<Home />} />
            <Route path="games/:slug" element={<GamePage />} />
            <Route path="games/:slug/edit" element={<EditGame />} />
            <Route path="new" element={<NewGame />} />
            <Route path="u/:username" element={<ProfilePage />} />
            <Route path="settings" element={<SettingsPage />} />
            <Route path="login" element={<LoginPage />} />
            <Route path="device" element={<DevicePage />} />
            <Route path="*" element={<NotFound />} />
          </Route>
        </Routes>
      </AuthProvider>
    </BrowserRouter>
    </ErrorBoundary>
  </StrictMode>,
)
