#!/bin/bash
# Quick deployment wrapper - calls the infrastructure deployment script

cd "$(dirname "$0")/infrastructure"
./deploy.sh
