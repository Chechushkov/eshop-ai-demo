# Contributing to eShop

Thank you for your interest in contributing to eShop! We're excited to collaborate with you and see how together we can improve and evolve this sample application.

## Language conventions for this fork

Use English for engineering work, including:

- Code identifiers, comments and XML documentation.
- Test descriptions, assertion failures and test-run summaries.
- Application logs, execution traces, validation diagnostics and technical exceptions.
- Repository documentation, architecture decision records and CI messages.
- Commit messages and pull request titles and descriptions.

The support page uses English for labels, controls, help text and error messages.
The current assistant prompts and knowledge documents still support the Russian
demo content. Saved questions, replies and conversation titles retain their
original language. Russian domain messages, test fixtures and text-matching
expressions may remain when they support that locale. Validation feedback is an
engineering diagnostic and uses English even when it is also sent to the model
for a repair attempt.

Comments should explain constraints, decisions and tradeoffs. Do not add comments
that merely restate the next line of code. Keep tool names, JSON property names,
source IDs and request identifiers stable when translating text.

A language-only change must preserve control flow, permission and ownership
checks, replay semantics and validation conditions. Run the support self-tests
after changing executable strings. If model feedback changes, also check a repair
attempt with the configured model: offline fakes cannot verify its wording.

## Getting Started

If this is your first visit, a great way to begin is by tackling issues tagged as `"help wanted"` or `"good first issue"`. These are specially curated to help you get acquainted with the project and make a meaningful impact early on.

## Spot a Typo?

Typos and other small fixes are important to us -— no contribution is too small! If you spot something small, go ahead and open a Pull Request. For these types of contributions, there's no need to create a separate issue.

## Have a Suggestion?

If you have a suggestion on how to enhance eShop, please open an issue with the following details:
- A clear title and description of the suggestion
- Any relevant examples or mockups
- Indicate whether you're interested in implementing the feature yourself

We'll review your suggestion and have a discussion about its potential inclusion in the project.

## Contribution Principles

When considering contributions, we're guided by several principles that align with the project's vision:

- **Best Practices**: We want this sample to be canonical and reflect the best practices in the industry and in .NET.

- **Selectivity in Tools and Libraries**: There is a rich ecosystem of tools, projects, and libraries out there, and we cannot use all of them in this sample. We would like this repo to reflect the use of a realistic set of technologies, not to be a showcase or example of every possible thing it could use.

- **Architectural Integrity**: We welcome refactoring and architectural improvements, provided they're justified. Large-scale changes should come with a clear rationale, such as significant enhancements to the application's design or performance.

- **Enhancing Reliability and Scalability**: We welcome contributions that improve the application's reliability and scalability. These could include updates to error handling, redundancy mechanisms, data access, and any other changes that help eShop operate more robustly under load. We'd love to see relevant test scenarios or metrics for these contributions.

- **Performance Enhancements**: If you're looking to speed up eShop, we're all for it! Please include benchmark comparisons to demonstrate the improvements. Performance improvements that make the code less readable or canonical may have more scrutiny applied.

## Code of Conduct

To ensure a welcoming and positive environment for everyone, please adhere to our Code of Conduct. Respectful collaboration is key to a successful project.
