import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import prettier from 'eslint-config-prettier'

const reactHooksRecommended =
  reactHooks.configs?.flat?.recommended ?? reactHooks.configs['recommended-latest']

export default tseslint.config(
  { ignores: ['dist', 'dev-dist', 'node_modules', 'coverage', 'public'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      ...tseslint.configs.recommended,
      reactHooksRecommended,
      reactRefresh.configs.vite,
      prettier,
    ],
    languageOptions: {
      ecmaVersion: 2023,
      globals: globals.browser,
    },
    rules: {
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      // shadcn/ui-style modules co-export variants/hooks next to components;
      // allow constant exports and disable the rule for the ui kit below.
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    },
  },
  {
    // ui kit + domain chips co-export cva variants / pure helpers by design.
    files: ['src/components/ui/**/*.tsx', 'src/components/domain/**/*.tsx', 'src/app/router.tsx'],
    rules: {
      'react-refresh/only-export-components': 'off',
    },
  },
  {
    files: ['scripts/**/*.mjs', '*.config.{js,ts}'],
    languageOptions: {
      globals: globals.node,
    },
  },
)
