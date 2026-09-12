/// Tier 1 — Semantic Model. The project and activity-type catalogue.
///
/// Shapes follow `schemas/domain/project.schema.json` and
/// `schemas/domain/activity-type.schema.json`, which the repository already
/// defines with identical fields: `id`, `name`, `active`, `version`. Those
/// schemas are the contract, so the domain matches them rather than inventing
/// a parallel shape.
///
/// The schemas' `active: boolean` is exactly the distinction DF-TE-0007 needed
/// for OQ-1 ("may inactive projects receive new time?"), which is a useful
/// corroboration: the decision was not an invention, it was already implied by
/// the contract.
module TimeEntry.Semantic.Catalogue

open System
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values

/// A catalogue entry's display name. Bounded at 120 characters because the
/// schemas say `maxLength: 120`.
type CatalogueName =
    private
    | CatalogueName of string

    member this.Value = let (CatalogueName v) = this in v

[<Literal>]
let MaxNameLength = 120

module CatalogueName =
    let create (raw: string) : Result<CatalogueName, TextError> =
        if String.IsNullOrWhiteSpace raw then Error TextEmpty
        elif raw.Trim().Length > MaxNameLength then
            Error(TextTooLong(raw.Trim().Length, MaxNameLength))
        else
            Ok(CatalogueName(raw.Trim()))

    let value (CatalogueName v) = v

/// Whether a catalogue entry may be named by *new* work.
///
/// `Available`/`Archived` rather than `Active`/`Inactive` to avoid colliding
/// with `EntryState.Active`, which means something different: an entry that
/// counts toward totals.
type CatalogueStatus =
    | Available
    | Archived

module CatalogueStatus =
    /// The schemas carry a boolean, so this is the mapping both ways.
    let ofActiveFlag (isActive: bool) = if isActive then Available else Archived

    let toActiveFlag (status: CatalogueStatus) =
        match status with
        | Available -> true
        | Archived -> false

/// The schemas title these types "Project projection" and "Activity type
/// projection", and require a `version` field. Both facts matter:
///
/// - *Projection* means the catalogue is upstream output this application
///   reads, not state it owns. There is no write path for it here.
/// - The required `version` is carried rather than dropped. The domain never
///   acts on it, but a ledger whose whole purpose is auditability should be
///   able to say which catalogue version a classification was made against,
///   and silently discarding a field the contract requires is how that
///   becomes impossible later.
type Project =
    { Id: ProjectId
      Name: CatalogueName
      Status: CatalogueStatus
      ProjectionVersion: VersionToken }

type ActivityType =
    { Id: ActivityTypeId
      Name: CatalogueName
      Status: CatalogueStatus
      ProjectionVersion: VersionToken }

/// The loaded catalogue.
///
/// Held as maps because every transition looks entries up by id, and a list
/// scan would make the guard's cost grow with the catalogue.
type Catalogue =
    { Projects: Map<string, Project>
      ActivityTypes: Map<string, ActivityType> }

/// Why a catalogue reference is not usable for new work.
type CatalogueError =
    | ProjectNotInCatalogue of ProjectId
    /// DF-TE-0007: an archived project rejects new time. Existing entries
    /// referencing it keep counting, which is why this is checked on the
    /// *command*, never on stored state.
    | ProjectIsArchived of ProjectId
    | ActivityTypeNotInCatalogue of ActivityTypeId
    | ActivityTypeIsArchived of ActivityTypeId

module Catalogue =

    let empty =
        { Projects = Map.empty
          ActivityTypes = Map.empty }

    let ofLists (projects: Project list) (activityTypes: ActivityType list) =
        { Projects = projects |> List.map (fun p -> ProjectId.value p.Id, p) |> Map.ofList
          ActivityTypes =
            activityTypes
            |> List.map (fun a -> ActivityTypeId.value a.Id, a)
            |> Map.ofList }

    let tryProject (projectId: ProjectId) (catalogue: Catalogue) =
        catalogue.Projects |> Map.tryFind (ProjectId.value projectId)

    let tryActivityType (activityTypeId: ActivityTypeId) (catalogue: Catalogue) =
        catalogue.ActivityTypes |> Map.tryFind (ActivityTypeId.value activityTypeId)

    /// Whether a project may be named by new or corrected work.
    let projectAcceptsNewTime (projectId: ProjectId) (catalogue: Catalogue) : Result<unit, CatalogueError> =
        match tryProject projectId catalogue with
        | None -> Error(ProjectNotInCatalogue projectId)
        | Some project ->
            match project.Status with
            | Available -> Ok()
            | Archived -> Error(ProjectIsArchived projectId)

    let activityTypeAcceptsNewTime
        (activityTypeId: ActivityTypeId)
        (catalogue: Catalogue)
        : Result<unit, CatalogueError> =
        match tryActivityType activityTypeId catalogue with
        | None -> Error(ActivityTypeNotInCatalogue activityTypeId)
        | Some activityType ->
            match activityType.Status with
            | Available -> Ok()
            | Archived -> Error(ActivityTypeIsArchived activityTypeId)

    /// Both references a command's facts must satisfy.
    let acceptsNewTime
        (projectId: ProjectId)
        (activityTypeId: ActivityTypeId)
        (catalogue: Catalogue)
        : Result<unit, CatalogueError> =
        projectAcceptsNewTime projectId catalogue
        |> Result.bind (fun () -> activityTypeAcceptsNewTime activityTypeId catalogue)
