import { z } from 'zod'

// ---------------------------------------------------------------------------
// Security section logic: change-password schema and the TOTP enrolment flow
// state machine (kept as a pure reducer so it is unit-testable).
// ---------------------------------------------------------------------------

export const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, 'Enter your current password.'),
    newPassword: z
      .string()
      .min(8, 'At least 8 characters.')
      .max(256, 'At most 256 characters.'),
    confirmPassword: z.string(),
  })
  .refine((v) => v.newPassword === v.confirmPassword, {
    message: 'Passwords do not match.',
    path: ['confirmPassword'],
  })
  .refine((v) => v.newPassword !== v.currentPassword, {
    message: 'Pick a password you have not just typed above.',
    path: ['newPassword'],
  })

export type ChangePasswordForm = z.infer<typeof changePasswordSchema>

// ------------------------------------------------------------- TOTP flow

export interface TotpFlow {
  step: 'idle' | 'setup' | 'recovery' | 'disable'
  secret: string | null
  otpauthUri: string | null
  code: string
  recoveryCodes: string[] | null
  error: string | null
  busy: boolean
}

export const initialTotpFlow: TotpFlow = {
  step: 'idle',
  secret: null,
  otpauthUri: null,
  code: '',
  recoveryCodes: null,
  error: null,
  busy: false,
}

export type TotpFlowAction =
  | { type: 'begin-setup' }
  | { type: 'setup-ready'; secret: string; otpauthUri: string }
  | { type: 'code-input'; code: string }
  | { type: 'submit' }
  | { type: 'enabled'; recoveryCodes: string[] }
  | { type: 'begin-disable' }
  | { type: 'disabled' }
  | { type: 'failed'; error: string }
  | { type: 'cancel' }

export function totpReducer(state: TotpFlow, action: TotpFlowAction): TotpFlow {
  switch (action.type) {
    case 'begin-setup':
      return { ...initialTotpFlow, busy: true }
    case 'setup-ready':
      return {
        ...initialTotpFlow,
        step: 'setup',
        secret: action.secret,
        otpauthUri: action.otpauthUri,
      }
    case 'code-input':
      // Codes are 6-digit TOTP or dash-separated recovery codes on disable.
      return { ...state, code: action.code, error: null }
    case 'submit':
      return { ...state, busy: true, error: null }
    case 'enabled':
      // Secrets leave the state as soon as enrolment succeeds; the recovery
      // codes are shown exactly once.
      return { ...initialTotpFlow, step: 'recovery', recoveryCodes: action.recoveryCodes }
    case 'begin-disable':
      return { ...initialTotpFlow, step: 'disable' }
    case 'disabled':
      return initialTotpFlow
    case 'failed':
      return { ...state, busy: false, error: action.error }
    case 'cancel':
      return initialTotpFlow
  }
}
