import { useState } from 'react'
import { AlertTriangleIcon, DownloadIcon } from 'lucide-react'
import { useDispatch, useSelector } from 'react-redux'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'

import { useGetMeQuery } from '@/api/settingsApi'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { selectAccessToken, sessionCleared } from '@/features/auth/authSlice'

async function readApiError(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { error?: { message?: string } }
    return body.error?.message ?? fallback
  } catch {
    return response.status === 404 ? 'This endpoint is not available yet.' : fallback
  }
}

function DownloadCard() {
  const accessToken = useSelector(selectAccessToken)
  const [downloading, setDownloading] = useState(false)

  const download = async () => {
    setDownloading(true)
    try {
      const response = await fetch('/api/me/export', {
        headers: accessToken ? { authorization: `Bearer ${accessToken}` } : undefined,
      })
      if (!response.ok) {
        toast.error(await readApiError(response, 'Export failed — try again.'))
        return
      }
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = 'edgewise-export.zip'
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
    } catch {
      toast.error('Cannot reach the server. Check your connection.')
    } finally {
      setDownloading(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Download my data</CardTitle>
        <CardDescription>
          Everything you have put into Edgewise — trades, plans, journal entries, portfolio and
          settings — as a ZIP of CSV/JSON files. Your data stays yours.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Button onClick={() => void download()} disabled={downloading}>
          <DownloadIcon /> {downloading ? 'Preparing export…' : 'Download my data'}
        </Button>
      </CardContent>
    </Card>
  )
}

function DangerZoneCard() {
  const { data: me } = useGetMeQuery()
  const accessToken = useSelector(selectAccessToken)
  const dispatch = useDispatch()
  const navigate = useNavigate()

  const [open, setOpen] = useState(false)
  const [emailConfirm, setEmailConfirm] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)

  const emailMatches = !!me && emailConfirm.trim().toLowerCase() === me.email.toLowerCase()

  const close = () => {
    setOpen(false)
    setEmailConfirm('')
    setPassword('')
  }

  const deleteAccount = async () => {
    setBusy(true)
    try {
      const response = await fetch('/api/me', {
        method: 'DELETE',
        headers: {
          'content-type': 'application/json',
          ...(accessToken ? { authorization: `Bearer ${accessToken}` } : {}),
        },
        body: JSON.stringify({ password }),
      })
      if (response.status === 204) {
        dispatch(sessionCleared())
        void navigate('/login')
        return
      }
      toast.error(await readApiError(response, 'Could not delete your account.'))
    } catch {
      toast.error('Cannot reach the server. Check your connection.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="border-destructive/40">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base text-destructive">
          <AlertTriangleIcon className="size-4" aria-hidden /> Danger zone
        </CardTitle>
        <CardDescription>
          Permanently delete your account and every trade, plan, journal entry and setting in it.
          There is no undo — export your data first.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Button variant="destructive" onClick={() => setOpen(true)}>
          Delete my account
        </Button>

        <Dialog open={open} onOpenChange={(next) => !next && close()}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Delete account</DialogTitle>
              <DialogDescription>
                This permanently erases everything. Type your email and current password to
                confirm.
              </DialogDescription>
            </DialogHeader>
            <div className="flex flex-col gap-3">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="del-email">
                  Type <span className="font-mono">{me?.email ?? 'your email'}</span> to confirm
                </Label>
                <Input
                  id="del-email"
                  autoComplete="off"
                  value={emailConfirm}
                  onChange={(e) => setEmailConfirm(e.target.value)}
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="del-password">Current password</Label>
                <Input
                  id="del-password"
                  type="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </div>
            </div>
            <DialogFooter>
              <Button variant="ghost" onClick={close}>
                Cancel
              </Button>
              <Button
                variant="destructive"
                disabled={!emailMatches || !password || busy}
                onClick={() => void deleteAccount()}
              >
                {busy ? 'Deleting…' : 'Delete everything'}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </CardContent>
    </Card>
  )
}

export function ExportSection() {
  return (
    <div className="flex flex-col gap-4">
      <DownloadCard />
      <DangerZoneCard />
    </div>
  )
}
