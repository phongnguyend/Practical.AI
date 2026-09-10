import { useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { FileText, GitBranch, LayoutDashboard, Monitor, Moon, Search, Sun } from 'lucide-react'
import OverviewPage from './pages/OverviewPage'
import IndexedFilesPage from './pages/IndexedFilesPage'
import DeltaStatePage from './pages/DeltaStatePage'
import SearchPage from './pages/SearchPage'

type Theme = 'system' | 'light' | 'dark'

const THEME_KEY = 'sp-viewer-theme'
const THEME_ORDER: Theme[] = ['system', 'light', 'dark']
const THEME_LABELS: Record<Theme, string> = { system: 'System', light: 'Light', dark: 'Dark' }
const THEME_ICONS: Record<Theme, typeof Monitor> = { system: Monitor, light: Sun, dark: Moon }

function readTheme(): Theme {
  const stored = localStorage.getItem(THEME_KEY)
  return stored === 'light' || stored === 'dark' ? stored : 'system'
}

export default function App() {
  const [theme, setTheme] = useState<Theme>(readTheme)

  useEffect(() => {
    if (theme === 'system') {
      document.documentElement.removeAttribute('data-theme')
      localStorage.removeItem(THEME_KEY)
    } else {
      document.documentElement.setAttribute('data-theme', theme)
      localStorage.setItem(THEME_KEY, theme)
    }
  }, [theme])

  const ThemeIcon = THEME_ICONS[theme]

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          <strong>SharePoint → Azure AI Search</strong>
          <span>viewer</span>
        </div>
        <nav className="nav">
          <NavLink to="/overview">
            <LayoutDashboard size={16} />
            Overview
          </NavLink>
          <NavLink to="/files">
            <FileText size={16} />
            Indexed files
          </NavLink>
          <NavLink to="/delta">
            <GitBranch size={16} />
            Delta state
          </NavLink>
          <NavLink to="/search">
            <Search size={16} />
            Search
          </NavLink>
        </nav>
        <button
          className="ghost"
          title="Switch between system, light, and dark"
          onClick={() => setTheme(THEME_ORDER[(THEME_ORDER.indexOf(theme) + 1) % THEME_ORDER.length])}
        >
          <ThemeIcon size={15} />
          {THEME_LABELS[theme]}
        </button>
      </header>

      <main className="main">
        <Routes>
          <Route path="/" element={<Navigate to="/overview" replace />} />
          <Route path="/overview" element={<OverviewPage />} />
          <Route path="/files" element={<IndexedFilesPage />} />
          <Route path="/delta" element={<DeltaStatePage />} />
          <Route path="/search" element={<SearchPage />} />
          <Route path="*" element={<Navigate to="/overview" replace />} />
        </Routes>
      </main>
    </div>
  )
}
