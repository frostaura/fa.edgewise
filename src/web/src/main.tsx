import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Provider } from 'react-redux'
import { RouterProvider } from 'react-router/dom'

import '@/index.css'
import { store } from '@/app/store'
import { bootstrap } from '@/app/bootstrap'
import { router } from '@/app/router'
import { Toaster } from '@/components/ui/sonner'

bootstrap(store)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Provider store={store}>
      <RouterProvider router={router} />
      <Toaster position="bottom-right" />
    </Provider>
  </StrictMode>,
)
