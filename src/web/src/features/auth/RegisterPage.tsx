import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Link, Navigate, useNavigate } from 'react-router'
import { Loader2Icon } from 'lucide-react'
import { z } from 'zod'

import { useRegisterMutation } from '@/api/authApi'
import { getApiErrorMessage } from '@/api/types'
import { useAppSelector } from '@/app/hooks'
import { selectIsAuthenticated } from '@/features/auth/authSlice'
import { AuthCard, AuthError } from '@/features/auth/AuthCard'
import { Button } from '@/components/ui/button'
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form'
import { Input } from '@/components/ui/input'

const schema = z
  .object({
    email: z.email('Enter a valid email address'),
    password: z.string().min(8, 'Use at least 8 characters'),
    confirmPassword: z.string(),
  })
  .refine((values) => values.password === values.confirmPassword, {
    message: 'Passwords do not match',
    path: ['confirmPassword'],
  })

type FormValues = z.infer<typeof schema>

export default function RegisterPage() {
  const isAuthenticated = useAppSelector(selectIsAuthenticated)
  const navigate = useNavigate()
  const [register, { isLoading }] = useRegisterMutation()
  const [apiError, setApiError] = useState<string | null>(null)

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: '', password: '', confirmPassword: '' },
  })

  if (isAuthenticated) return <Navigate to="/" replace />

  const onSubmit = async (values: FormValues) => {
    setApiError(null)
    try {
      await register({ email: values.email, password: values.password }).unwrap()
      void navigate('/', { replace: true })
    } catch (err) {
      setApiError(getApiErrorMessage(err, 'Registration failed. Please try again.'))
    }
  }

  return (
    <AuthCard
      title="Create your account"
      description="Start journaling your process in minutes."
      footer={
        <span>
          Already have an account?{' '}
          <Link to="/login" className="font-medium text-primary underline-offset-4 hover:underline">
            Sign in
          </Link>
        </span>
      }
    >
      <Form {...form}>
        <form onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4" noValidate>
          <AuthError message={apiError} />
          <FormField
            control={form.control}
            name="email"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Email</FormLabel>
                <FormControl>
                  <Input
                    type="email"
                    autoComplete="email"
                    placeholder="you@example.com"
                    autoFocus
                    {...field}
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="password"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Password</FormLabel>
                <FormControl>
                  <Input type="password" autoComplete="new-password" {...field} />
                </FormControl>
                <FormDescription>At least 8 characters.</FormDescription>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="confirmPassword"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Confirm password</FormLabel>
                <FormControl>
                  <Input type="password" autoComplete="new-password" {...field} />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <Button type="submit" className="mt-2 w-full" disabled={isLoading}>
            {isLoading && <Loader2Icon className="animate-spin" aria-hidden />}
            Create account
          </Button>
        </form>
      </Form>
    </AuthCard>
  )
}
