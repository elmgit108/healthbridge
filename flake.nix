{
  description = "HealthBridge — pinned development toolchain for the Go gateway, the C# HL7/DICOM service and the Python monitoring service, plus the Kubernetes, Terraform and image-scanning tools this repo uses";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs?ref=nixos-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      system = "aarch64-darwin";

      # Not nixpkgs.legacyPackages.${system}, because that comes with the default
      # config and Terraform is BSL-licensed — nixpkgs refuses to build unfree
      # packages unless asked. Importing nixpkgs ourselves lets us allow it.
      # Replacing terraform with opentofu below (MPL, free) removes the need.
      pkgs = import nixpkgs {
        inherit system;
        config.allowUnfree = true;
      };
    in
    {
      # 'nix develop'  — or automatically on cd, via direnv and .envrc
      devShells.${system}.default = pkgs.mkShell {
        packages = [
          # Language toolchains. Keep these matched to the Dockerfiles, or local
          # tests and the built image drift apart.
          pkgs.go_1_25 # gateway/Dockerfile             golang:1.25-alpine
          pkgs.govulncheck # vulnerability scan for go
          pkgs.dotnet-sdk_8 # hl7-service/Dockerfile         dotnet/sdk:8.0
          pkgs.python311 # monitoring-service/Dockerfile  python:3.11-slim
          pkgs.pip-audit # vulnerability scan for python
          # Cluster and infrastructure tooling.
          pkgs.kubectl
          pkgs.kubernetes-helm # NB: 'helm' in nixpkgs is an unrelated package
          pkgs.kind
          pkgs.terraform
          pkgs.trivy # container image scanning — see MERGE-PLAN.md phase 5
        ];
      };

      # 'nix fmt' — formats the Nix files in this repo.
      formatter.${system} = pkgs.nixpkgs-fmt;
    };
}
