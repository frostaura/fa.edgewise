import * as React from 'react'
import { Link } from 'react-router'
import { ArrowLeftIcon, CheckCircle2Icon, FileUpIcon, UploadIcon } from 'lucide-react'
import { toast } from 'sonner'

import { useImportCommitMutation, useImportPreviewMutation } from '@/api/journalApi'
import { getApiErrorMessage } from '@/api/types'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { cn } from '@/lib/utils'
import {
  MAPPING_FIELDS,
  REQUIRED_FIELDS,
  importWizardReducer,
  initialWizardState,
  mappingComplete,
  type MappingField,
} from '@/features/journal/importMapping'

const NONE = '__none__'
const VENUES = ['generic', 'binance', 'coinbase', 'easyEquities'] as const

const FIELD_LABELS: Record<MappingField, string> = {
  timestamp: 'Timestamp',
  symbol: 'Symbol',
  side: 'Side',
  qty: 'Quantity',
  price: 'Price',
  fee: 'Fee',
  feeCurrency: 'Fee currency',
}

export default function ImportPage() {
  const [state, dispatch] = React.useReducer(importWizardReducer, initialWizardState)
  const [preview, { isLoading: previewing }] = useImportPreviewMutation()
  const [commit, { isLoading: committing, data: result }] = useImportCommitMutation()
  const [accountId, setAccountId] = React.useState('')
  const [dragOver, setDragOver] = React.useState(false)
  const inputRef = React.useRef<HTMLInputElement>(null)

  const loadPreview = async (file: File) => {
    dispatch({ type: 'filePicked', file })
    try {
      const previewData = await preview({ file }).unwrap()
      dispatch({ type: 'previewLoaded', preview: previewData })
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not parse the file'))
    }
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    const file = e.dataTransfer.files[0]
    if (file) void loadPreview(file)
  }

  const runCommit = async () => {
    if (!state.file) return
    try {
      await commit({
        file: state.file,
        mapping: state.mapping,
        accountId,
        venue: state.venue,
        saveMappingAs: state.saveMappingAs || undefined,
      }).unwrap()
      dispatch({ type: 'committed' })
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Import failed'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2 mb-2">
          <Link to="/journal">
            <ArrowLeftIcon aria-hidden />
            Journal
          </Link>
        </Button>
        <PageHeader
          title="Import"
          description="Bring in fills from broker CSV exports. Imports are idempotent — re-running a file skips duplicates."
        />
      </div>

      {/* Step 1 — upload */}
      {state.step === 'upload' && (
        <button
          type="button"
          className={cn(
            'flex min-h-56 w-full flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed bg-surface/50 px-6 py-12 text-center transition-colors',
            dragOver ? 'border-primary bg-primary/5' : 'border-border',
          )}
          onClick={() => inputRef.current?.click()}
          onDragOver={(e) => {
            e.preventDefault()
            setDragOver(true)
          }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
        >
          <div className="flex size-12 items-center justify-center rounded-full bg-muted">
            <UploadIcon className="size-6 text-muted-foreground" aria-hidden />
          </div>
          <span className="text-base font-semibold">
            {previewing ? 'Reading file…' : 'Drop a CSV here or click to browse'}
          </span>
          <span className="max-w-md text-sm text-muted-foreground">
            Binance trade history, Coinbase fills, EasyEquities statements or any generic
            timestamp/symbol/side/qty/price export. Up to 25 MB.
          </span>
          <input
            ref={inputRef}
            type="file"
            accept=".csv,text/csv"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void loadPreview(file)
            }}
          />
        </button>
      )}

      {/* Step 2 — mapping */}
      {state.step === 'map' && state.preview && (
        <div className="flex flex-col gap-4">
          <Card className="gap-3 py-4">
            <CardHeader className="flex-row flex-wrap items-center justify-between gap-2 px-4">
              <CardTitle className="flex items-center gap-2 text-sm">
                <FileUpIcon className="size-4" aria-hidden />
                {state.file?.name}
                <Badge variant="secondary">{state.preview.columns.length} columns</Badge>
              </CardTitle>
              <span className="flex items-center gap-2">
                <Label className="text-xs text-muted-foreground">Venue preset</Label>
                <Select
                  value={state.venue}
                  onValueChange={(v) => dispatch({ type: 'setVenue', venue: v })}
                >
                  <SelectTrigger className="h-8 text-xs">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {VENUES.map((v) => (
                      <SelectItem key={v} value={v}>
                        {v}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </span>
            </CardHeader>
            <CardContent className="flex flex-col gap-4 px-4">
              {state.preview.savedMappings.length > 0 && (
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-xs text-muted-foreground">Saved mappings:</span>
                  {state.preview.savedMappings.map((m) => (
                    <Button
                      key={m.id}
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        dispatch({
                          type: 'applySavedMapping',
                          mappingJson: m.mappingJson,
                          venue: m.venue,
                        })
                      }
                    >
                      {m.name}
                    </Button>
                  ))}
                </div>
              )}

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
                {MAPPING_FIELDS.map((field) => (
                  <div key={field} className="flex flex-col gap-1">
                    <Label className="text-xs">
                      {FIELD_LABELS[field]}
                      {REQUIRED_FIELDS.includes(field) && (
                        <span className="text-destructive"> *</span>
                      )}
                    </Label>
                    <Select
                      value={state.mapping[field] ?? NONE}
                      onValueChange={(v) =>
                        dispatch({ type: 'setField', field, column: v === NONE ? null : v })
                      }
                    >
                      <SelectTrigger className="h-8 w-full text-xs">
                        <SelectValue placeholder="—" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={NONE}>— not mapped —</SelectItem>
                        {state.preview!.columns.map((column) => (
                          <SelectItem key={column} value={column}>
                            {column}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                ))}
              </div>

              {/* Sample preview */}
              <div className="overflow-x-auto rounded-lg border">
                <Table>
                  <TableHeader>
                    <TableRow>
                      {state.preview.columns.map((column) => (
                        <TableHead key={column} className="text-xs whitespace-nowrap">
                          {column}
                        </TableHead>
                      ))}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {state.preview.sampleRows.slice(0, 5).map((row, i) => (
                      <TableRow key={i}>
                        {state.preview!.columns.map((column) => (
                          <TableCell key={column} className="text-xs whitespace-nowrap">
                            {row[column]}
                          </TableCell>
                        ))}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            </CardContent>
          </Card>

          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <Label htmlFor="import-account" className="text-xs">
                Account id <span className="text-destructive">*</span>
              </Label>
              <Input
                id="import-account"
                className="h-9 w-72 font-mono text-xs"
                placeholder="account UUID these fills belong to"
                value={accountId}
                onChange={(e) => setAccountId(e.target.value)}
              />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="import-save-as" className="text-xs">
                Save mapping as
              </Label>
              <Input
                id="import-save-as"
                className="h-9 w-56"
                placeholder="optional name"
                value={state.saveMappingAs}
                onChange={(e) => dispatch({ type: 'setSaveMappingAs', name: e.target.value })}
              />
            </div>
            <Button
              onClick={() => void runCommit()}
              disabled={committing || !mappingComplete(state.mapping) || accountId.trim() === ''}
            >
              {committing ? 'Importing…' : 'Import fills'}
            </Button>
            <Button variant="ghost" onClick={() => dispatch({ type: 'reset' })}>
              Start over
            </Button>
          </div>
          {!mappingComplete(state.mapping) && (
            <p className="text-xs text-warning">
              Map every required column (timestamp, symbol, side, quantity, price) to continue.
            </p>
          )}
        </div>
      )}

      {/* Step 3 — results */}
      {state.step === 'done' && result && (
        <Card className="gap-3 py-6">
          <CardContent className="flex flex-col items-center gap-3 text-center">
            <CheckCircle2Icon className="size-10 text-success" aria-hidden />
            <p className="text-lg font-semibold">
              {result.imported} fill{result.imported === 1 ? '' : 's'} imported
            </p>
            <p className="text-sm text-muted-foreground">
              {result.duplicates} duplicate{result.duplicates === 1 ? '' : 's'} skipped
              {result.errors.length > 0 && ` · ${result.errors.length} row error(s)`}
            </p>
            {result.errors.length > 0 && (
              <ul className="max-h-40 w-full max-w-lg overflow-y-auto rounded-lg bg-muted/50 p-3 text-left text-xs text-muted-foreground">
                {result.errors.slice(0, 20).map((error, i) => (
                  <li key={i}>{error}</li>
                ))}
              </ul>
            )}
            <div className="flex gap-2">
              <Button asChild>
                <Link to="/journal/inbox">Review in Inbox</Link>
              </Button>
              <Button variant="outline" onClick={() => dispatch({ type: 'reset' })}>
                Import another file
              </Button>
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
